using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FoundrySettings>(builder.Configuration.GetSection("Foundry"));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<IMissingFieldService, MissingFieldService>();
builder.Services.AddSingleton<IAiExtractionService, FoundryExtractionService>();
builder.Services.AddSingleton<IFileTextExtractor, FileTextExtractor>();

var sqlConnectionString =
    builder.Configuration.GetConnectionString("Drafts")
    ?? builder.Configuration["Storage:ConnectionString"];

if (!string.IsNullOrWhiteSpace(sqlConnectionString))
{
    builder.Services.AddSingleton<ISessionStore>(_ => new SqlSessionStore(sqlConnectionString));
}
else
{
    builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
}

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/draft/session/{sessionId}", async (
    string sessionId,
    ISessionStore store,
    IMissingFieldService missingFieldService,
    CancellationToken cancellationToken) =>
{
    var session = await store.GetSessionAsync(sessionId, cancellationToken);
    if (session is null)
    {
        return Results.NotFound(new { error = "Session not found." });
    }

    if (session.RequiresValidation)
    {
        return Results.BadRequest(new { error = "Validation is pending. Call /draft/validate before confirming." });
    }

    if (session.RequiresValidation)
    {
        return Results.BadRequest(new { error = "Validation is pending. Call POST /draft/validate first." });
    }

    if (session.Status == DraftStatus.Hold)
    {
        return Results.BadRequest(new { error = "Session is on hold. Resume by calling /draft/validate with accepted=true and optional corrections." });
    }

    var missing = missingFieldService.GetMissingFields(session.Draft)
        .Where(field => !session.SkippedFields.Contains(field))
        .ToList();

    return Results.Ok(DraftResult.FromSession(session, missing, null));
});

app.MapPost("/draft/create", async (
    DraftCreateRequest request,
    ISessionStore store,
    IAiExtractionService extractionService,
    IMissingFieldService missingFieldService,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    if (request.InputMode != InputMode.Text)
    {
        return Results.BadRequest(new { error = "Use /draft/from-file for file ingestion." });
    }

    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.InputText))
    {
        return Results.BadRequest(new { error = "userId and inputText are required for text ingestion." });
    }

    var logger = loggerFactory.CreateLogger("DraftCreate");
    var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
        ? Guid.NewGuid().ToString("N")
        : request.SessionId;

    var session = await store.GetSessionAsync(sessionId, cancellationToken)
        ?? DraftSession.CreateNew(sessionId, request.UserId, InputMode.Text, null, null);

    session.CurrentStep = "extraction";
    session.InputMode = InputMode.Text;
    session.RequiresValidation = false;
    await store.SaveMessageAsync(sessionId, "user", request.InputText!, cancellationToken);

    var extracted = await extractionService.ExtractAsync(request.InputText!, session.Draft, cancellationToken);
    session.Draft = DraftMerger.Merge(session.Draft, extracted);

    var missingFields = missingFieldService.GetMissingFields(session.Draft)
        .Where(field => !session.SkippedFields.Contains(field))
        .ToList();

    if (missingFields.Count > 0)
    {
        session.Status = DraftStatus.InProgress;
        session.CompletionState = "partial";
        session.CurrentStep = "clarification";
        session.PendingField = missingFields[0];
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var question = missingFieldService.BuildQuestion(session.PendingField);
        await store.SaveMessageAsync(sessionId, "assistant", question, cancellationToken);
        await store.UpsertSessionAsync(session, cancellationToken);

        logger.LogInformation("Draft session {SessionId} extracted with {MissingCount} missing fields", sessionId, missingFields.Count);

        return Results.Ok(DraftResult.FromSession(session, missingFields, question));
    }

    session.Status = DraftStatus.Review;
    session.CompletionState = "ready_for_review";
    session.CurrentStep = "review";
    session.PendingField = null;
    session.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await store.UpsertSessionAsync(session, cancellationToken);

    return Results.Ok(DraftResult.FromSession(session, [], null));
});

app.MapPost("/draft/from-text", async (
    DraftFromTextRequest request,
    ISessionStore store,
    IAiExtractionService extractionService,
    IMissingFieldService missingFieldService,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.InputText))
    {
        return Results.BadRequest(new { error = "userId and inputText are required." });
    }

    var logger = loggerFactory.CreateLogger("DraftFromText");
    var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
        ? Guid.NewGuid().ToString("N")
        : request.SessionId;

    var session = await store.GetSessionAsync(sessionId, cancellationToken)
        ?? DraftSession.CreateNew(sessionId, request.UserId, InputMode.Text, null, null);

    session.CurrentStep = "extraction";
    session.InputMode = InputMode.Text;
    session.RequiresValidation = false;
    await store.SaveMessageAsync(sessionId, "user", request.InputText, cancellationToken);

    var extracted = await extractionService.ExtractAsync(request.InputText, session.Draft, cancellationToken);
    session.Draft = DraftMerger.Merge(session.Draft, extracted);

    var missingFields = missingFieldService.GetMissingFields(session.Draft)
        .Where(field => !session.SkippedFields.Contains(field))
        .ToList();

    if (missingFields.Count > 0)
    {
        session.Status = DraftStatus.InProgress;
        session.CompletionState = "partial";
        session.CurrentStep = "clarification";
        session.PendingField = missingFields[0];
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var question = missingFieldService.BuildQuestion(session.PendingField);
        await store.SaveMessageAsync(sessionId, "assistant", question, cancellationToken);
        await store.UpsertSessionAsync(session, cancellationToken);

        logger.LogInformation("Draft session {SessionId} extracted with {MissingCount} missing fields", sessionId, missingFields.Count);

        return Results.Ok(DraftResult.FromSession(session, missingFields, question));
    }

    session.Status = DraftStatus.Review;
    session.CompletionState = "ready_for_review";
    session.CurrentStep = "review";
    session.PendingField = null;
    session.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await store.UpsertSessionAsync(session, cancellationToken);

    return Results.Ok(DraftResult.FromSession(session, [], null));
});

app.MapPost("/draft/from-file", async (
    [FromForm] string userId,
    [FromForm] string? sessionId,
    [FromForm] IFormFile file,
    ISessionStore store,
    IAiExtractionService extractionService,
    IMissingFieldService missingFieldService,
    IFileTextExtractor fileTextExtractor,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(userId) || file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "userId and a non-empty file are required." });
    }

    var logger = loggerFactory.CreateLogger("DraftFromFile");
    var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId)
        ? Guid.NewGuid().ToString("N")
        : sessionId;

    var session = await store.GetSessionAsync(resolvedSessionId, cancellationToken)
        ?? DraftSession.CreateNew(resolvedSessionId, userId, InputMode.File, file.FileName, null);

    var uploadsRoot = Path.Combine(AppContext.BaseDirectory, "uploads", resolvedSessionId);
    Directory.CreateDirectory(uploadsRoot);
    var safeFileName = Path.GetFileName(file.FileName);
    var filePath = Path.Combine(uploadsRoot, safeFileName);

    await using (var output = File.Create(filePath))
    {
        await file.CopyToAsync(output, cancellationToken);
    }

    session.InputMode = InputMode.File;
    session.SourceFileName = safeFileName;
    session.SourceFileReference = filePath;
    session.CurrentStep = "extraction";

    await store.SaveMessageAsync(session.SessionId, "user", $"Uploaded file: {safeFileName}", cancellationToken);

    var extractedText = await fileTextExtractor.ExtractTextAsync(filePath, file.ContentType, cancellationToken);
    if (!string.IsNullOrWhiteSpace(extractedText))
    {
        var extracted = await extractionService.ExtractAsync(extractedText, session.Draft, cancellationToken);
        session.Draft = DraftMerger.Merge(session.Draft, extracted);
    }

    var missingFields = missingFieldService.GetMissingFields(session.Draft)
        .Where(field => !session.SkippedFields.Contains(field))
        .ToList();

    session.RequiresValidation = true;
    session.Status = DraftStatus.InProgress;
    session.CompletionState = "partial";
    session.CurrentStep = "validation";
    session.PendingField = null;
    session.UpdatedAtUtc = DateTimeOffset.UtcNow;

    var validationPrompt = "Please review the extracted draft and call POST /draft/validate with accepted=true (and optional corrections) before clarification continues.";
    await store.SaveMessageAsync(session.SessionId, "assistant", validationPrompt, cancellationToken);
    await store.UpsertSessionAsync(session, cancellationToken);

    logger.LogInformation("File ingestion completed for session {SessionId}. Extracted text length: {Length}", session.SessionId, extractedText.Length);
    return Results.Ok(DraftResult.FromSession(session, missingFields, validationPrompt));
})
    .DisableAntiforgery();

app.MapPost("/draft/validate", async (
    DraftValidateRequest request,
    ISessionStore store,
    IMissingFieldService missingFieldService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId))
    {
        return Results.BadRequest(new { error = "sessionId is required." });
    }

    var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
    if (session is null)
    {
        return Results.NotFound(new { error = "Session not found." });
    }

    if (!session.RequiresValidation)
    {
        return Results.BadRequest(new { error = "Validation is not pending for this session." });
    }

    if (request.Corrections is not null)
    {
        session.Draft = DraftMerger.Merge(session.Draft, request.Corrections);
    }

    if (!request.Accepted)
    {
        session.Status = DraftStatus.Hold;
        session.CompletionState = "hold";
        session.CurrentStep = "hold";
        session.PendingField = null;
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await store.UpsertSessionAsync(session, cancellationToken);
        await store.SaveMessageAsync(session.SessionId, "system", "Draft moved to hold during validation.", cancellationToken);
        return Results.Ok(DraftResult.FromSession(session, missingFieldService.GetMissingFields(session.Draft), null));
    }

    session.RequiresValidation = false;
    var missing = missingFieldService.GetMissingFields(session.Draft)
        .Where(field => !session.SkippedFields.Contains(field))
        .ToList();

    if (missing.Count > 0)
    {
        session.Status = DraftStatus.InProgress;
        session.CompletionState = "partial";
        session.CurrentStep = "clarification";
        session.PendingField = missing[0];
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var question = missingFieldService.BuildQuestion(session.PendingField);
        await store.SaveMessageAsync(session.SessionId, "assistant", question, cancellationToken);
        await store.UpsertSessionAsync(session, cancellationToken);
        return Results.Ok(DraftResult.FromSession(session, missing, question));
    }

    session.Status = DraftStatus.Review;
    session.CompletionState = "ready_for_review";
    session.CurrentStep = "review";
    session.PendingField = null;
    session.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await store.UpsertSessionAsync(session, cancellationToken);

    return Results.Ok(DraftResult.FromSession(session, [], null));
});

app.MapPost("/draft/respond", async (
    DraftRespondRequest request,
    ISessionStore store,
    IMissingFieldService missingFieldService,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId))
    {
        return Results.BadRequest(new { error = "sessionId is required." });
    }

    var logger = loggerFactory.CreateLogger("DraftRespond");
    var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
    if (session is null)
    {
        return Results.NotFound(new { error = "Session not found." });
    }

    if (string.IsNullOrWhiteSpace(session.PendingField))
    {
        return Results.BadRequest(new { error = "No pending clarification question for this session." });
    }

    if (!request.Skip && string.IsNullOrWhiteSpace(request.Answer))
    {
        return Results.BadRequest(new { error = "answer is required when skip is false." });
    }

    if (request.Skip)
    {
        session.SkippedFields.Add(session.PendingField);
        await store.SaveMessageAsync(session.SessionId, "user", $"Skipped field: {session.PendingField}", cancellationToken);
    }
    else
    {
        DraftFieldUpdater.ApplyUserAnswer(session.Draft, session.PendingField, request.Answer!);
        await store.SaveMessageAsync(session.SessionId, "user", request.Answer!, cancellationToken);
    }

    var remaining = missingFieldService.GetMissingFields(session.Draft)
        .Where(field => !session.SkippedFields.Contains(field))
        .ToList();

    if (remaining.Count > 0)
    {
        session.Status = DraftStatus.InProgress;
        session.CompletionState = "partial";
        session.CurrentStep = "clarification";
        session.PendingField = remaining[0];
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var question = missingFieldService.BuildQuestion(session.PendingField);
        await store.SaveMessageAsync(session.SessionId, "assistant", question, cancellationToken);
        await store.UpsertSessionAsync(session, cancellationToken);

        logger.LogInformation("Draft session {SessionId} updated; still missing {MissingCount} fields", session.SessionId, remaining.Count);
        return Results.Ok(DraftResult.FromSession(session, remaining, question));
    }

    session.Status = DraftStatus.Review;
    session.CompletionState = "ready_for_review";
    session.CurrentStep = "review";
    session.PendingField = null;
    session.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await store.UpsertSessionAsync(session, cancellationToken);
    return Results.Ok(DraftResult.FromSession(session, [], null));
});

app.MapPost("/draft/hold", async (
    DraftHoldRequest request,
    ISessionStore store,
    IMissingFieldService missingFieldService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId))
    {
        return Results.BadRequest(new { error = "sessionId is required." });
    }

    var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
    if (session is null)
    {
        return Results.NotFound(new { error = "Session not found." });
    }

    session.Status = DraftStatus.Hold;
    session.CompletionState = "hold";
    session.CurrentStep = "hold";
    session.PendingField = null;
    session.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await store.UpsertSessionAsync(session, cancellationToken);
    await store.SaveMessageAsync(session.SessionId, "system", request.Reason ?? "Draft put on hold by user.", cancellationToken);

    var missing = missingFieldService.GetMissingFields(session.Draft)
        .Where(field => !session.SkippedFields.Contains(field))
        .ToList();

    return Results.Ok(DraftResult.FromSession(session, missing, null));
});

app.MapPost("/draft/confirm", async (
    DraftConfirmRequest request,
    ISessionStore store,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId))
    {
        return Results.BadRequest(new { error = "sessionId is required." });
    }

    var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
    if (session is null)
    {
        return Results.NotFound(new { error = "Session not found." });
    }

    if (request.Corrections is not null)
    {
        session.Draft = DraftMerger.Merge(session.Draft, request.Corrections);
    }

    session.Status = DraftStatus.Completed;
    session.CompletionState = "completed";
    session.CurrentStep = "completed";
    session.PendingField = null;
    session.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await store.UpsertSessionAsync(session, cancellationToken);
    await store.SaveMessageAsync(session.SessionId, "system", "Draft confirmed by user.", cancellationToken);

    return Results.Ok(DraftResult.FromSession(session, [], null));
});

if (app.Services.GetRequiredService<ISessionStore>() is SqlSessionStore sqlStore)
{
    await sqlStore.EnsureSchemaAsync(CancellationToken.None);
}

app.Run();

public enum DraftStatus
{
    Started,
    InProgress,
    Hold,
    Review,
    Completed
}

public enum InputMode
{
    Text,
    File
}

public sealed class DraftCreateRequest
{
    public string UserId { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public InputMode InputMode { get; set; } = InputMode.Text;
    public string? InputText { get; set; }
}

public sealed class DraftFromTextRequest
{
    public string UserId { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string InputText { get; set; } = string.Empty;
}

public sealed class DraftRespondRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string? Answer { get; set; }
    public bool Skip { get; set; }
}

public sealed class DraftConfirmRequest
{
    public string SessionId { get; set; } = string.Empty;
    public MdrDraft? Corrections { get; set; }
}

public sealed class DraftValidateRequest
{
    public string SessionId { get; set; } = string.Empty;
    public bool Accepted { get; set; } = true;
    public MdrDraft? Corrections { get; set; }
}

public sealed class DraftHoldRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public sealed class DraftResult
{
    public string SessionId { get; init; } = string.Empty;
    public DraftStatus Status { get; init; }
    public string CompletionState { get; init; } = "partial";
    public string CurrentStep { get; init; } = "extraction";
    public InputMode InputMode { get; init; } = InputMode.Text;
    public bool RequiresValidation { get; init; }
    public string? SourceFileName { get; init; }
    public string? SourceFileReference { get; init; }
    public string? PendingField { get; init; }
    public string? NextQuestion { get; init; }
    public IReadOnlyList<string> MissingFields { get; init; } = [];
    public MdrDraft Draft { get; init; } = new();

    public static DraftResult FromSession(DraftSession session, IReadOnlyList<string> missingFields, string? nextQuestion)
    {
        return new DraftResult
        {
            SessionId = session.SessionId,
            Status = session.Status,
            CompletionState = session.CompletionState,
            CurrentStep = session.CurrentStep,
            InputMode = session.InputMode,
            RequiresValidation = session.RequiresValidation,
            SourceFileName = session.SourceFileName,
            SourceFileReference = session.SourceFileReference,
            PendingField = session.PendingField,
            NextQuestion = nextQuestion,
            MissingFields = missingFields,
            Draft = session.Draft
        };
    }
}

public sealed class DraftSession
{
    public string SessionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DraftStatus Status { get; set; } = DraftStatus.Started;
    public string CompletionState { get; set; } = "partial";
    public string CurrentStep { get; set; } = "extraction";
    public InputMode InputMode { get; set; } = InputMode.Text;
    public bool RequiresValidation { get; set; }
    public string? SourceFileName { get; set; }
    public string? SourceFileReference { get; set; }
    public string? PendingField { get; set; }
    public MdrDraft Draft { get; set; } = new();
    public HashSet<string> SkippedFields { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public static DraftSession CreateNew(
        string sessionId,
        string userId,
        InputMode inputMode,
        string? sourceFileName,
        string? sourceFileReference)
    {
        return new DraftSession
        {
            SessionId = sessionId,
            UserId = userId,
            Status = DraftStatus.Started,
            CompletionState = "partial",
            CurrentStep = "extraction",
            InputMode = inputMode,
            SourceFileName = sourceFileName,
            SourceFileReference = sourceFileReference,
            Draft = new MdrDraft { Status = "draft" }
        };
    }
}

public sealed class MdrDraft
{
    public string? ArrangementId { get; set; }
    public string? Country { get; set; }
    public string? Description { get; set; }
    public List<string> Entities { get; set; } = [];
    public string Status { get; set; } = "draft";
}

public interface IMissingFieldService
{
    IReadOnlyList<string> GetMissingFields(MdrDraft draft);
    string BuildQuestion(string fieldName);
}

public sealed class MissingFieldService : IMissingFieldService
{
    public IReadOnlyList<string> GetMissingFields(MdrDraft draft)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(draft.ArrangementId)) missing.Add(nameof(MdrDraft.ArrangementId));
        if (string.IsNullOrWhiteSpace(draft.Country)) missing.Add(nameof(MdrDraft.Country));
        if (string.IsNullOrWhiteSpace(draft.Description)) missing.Add(nameof(MdrDraft.Description));
        if (draft.Entities.Count == 0) missing.Add(nameof(MdrDraft.Entities));

        return missing;
    }

    public string BuildQuestion(string fieldName)
    {
        return fieldName switch
        {
            nameof(MdrDraft.ArrangementId) => "Can you provide the arrangement identifier? You can also skip this field.",
            nameof(MdrDraft.Country) => "Can you provide the country related to this arrangement? You can also skip this field.",
            nameof(MdrDraft.Description) => "Can you provide a short arrangement description? You can also skip this field.",
            nameof(MdrDraft.Entities) => "Can you list the involved entities (comma-separated)? You can also skip this field.",
            _ => $"Can you provide a value for {fieldName}? You can also skip this field."
        };
    }
}

public interface IAiExtractionService
{
    Task<MdrDraft> ExtractAsync(string inputText, MdrDraft? existingDraft, CancellationToken cancellationToken);
}

public sealed class FoundryExtractionService : IAiExtractionService
{
    private readonly FoundrySettings _settings;
    private readonly ILogger<FoundryExtractionService> _logger;

    public FoundryExtractionService(IConfiguration configuration, ILogger<FoundryExtractionService> logger)
    {
        _settings = configuration.GetSection("Foundry").Get<FoundrySettings>() ?? new FoundrySettings();
        _logger = logger;
    }

    public async Task<MdrDraft> ExtractAsync(string inputText, MdrDraft? existingDraft, CancellationToken cancellationToken)
    {
        var fallback = existingDraft ?? new MdrDraft { Status = "draft" };

        if (string.IsNullOrWhiteSpace(_settings.Endpoint) || string.IsNullOrWhiteSpace(_settings.Model))
        {
            _logger.LogWarning("Foundry configuration missing; returning empty extraction to avoid guessing.");
            return fallback;
        }

        try
        {
            var projectClient = new AIProjectClient(new Uri(_settings.Endpoint), new DefaultAzureCredential());
            var agent = projectClient.AsAIAgent(
                model: _settings.Model,
                name: _settings.AgentName,
                instructions: "You extract MDR draft fields from user text. Never guess. Unknown fields must be null or empty arrays.");

            var prompt = $$"""
                Extract MDR draft fields from this text and return JSON only.

                Rules:
                - Extract only explicitly stated values.
                - If a value is missing, set it to null (or [] for entities).
                - Do not infer or invent values.
                - Return ONLY valid JSON with this exact shape:
                {
                  "arrangementId": string|null,
                  "country": string|null,
                  "description": string|null,
                  "entities": string[],
                  "status": "draft"
                }

                Input:
                {{inputText}}
                """;

            var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
            if (TryParseDraftFromResponse(response.Text, out var parsed) && parsed is not null)
            {
                return parsed;
            }

            _logger.LogWarning("Could not parse extraction response as JSON. Returning fallback draft.");
            return fallback;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extraction call failed; returning fallback draft.");
            return fallback;
        }
    }

    private static bool TryParseDraftFromResponse(string responseText, out MdrDraft? draft)
    {
        draft = null;
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        var jsonText = responseText.Trim();
        var match = Regex.Match(jsonText, "\\{[\\s\\S]*\\}");
        if (match.Success)
        {
            jsonText = match.Value;
        }

        try
        {
            draft = JsonSerializer.Deserialize<MdrDraft>(jsonText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (draft is null)
            {
                return false;
            }

            draft.Entities ??= [];
            draft.Status = "draft";
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public interface IFileTextExtractor
{
    Task<string> ExtractTextAsync(string filePath, string? contentType, CancellationToken cancellationToken);
}

public sealed class FileTextExtractor : IFileTextExtractor
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".csv", ".log", ".xml"
    };

    public async Task<string> ExtractTextAsync(string filePath, string? contentType, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath);
        if (TextExtensions.Contains(extension))
        {
            return await File.ReadAllTextAsync(filePath, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return await File.ReadAllTextAsync(filePath, cancellationToken);
        }

        // Keep unsupported/binary formats empty so the system asks follow-up questions instead of guessing.
        return string.Empty;
    }
}

public static class DraftMerger
{
    public static MdrDraft Merge(MdrDraft? current, MdrDraft? incoming)
    {
        var result = current is null
            ? new MdrDraft()
            : new MdrDraft
            {
                ArrangementId = current.ArrangementId,
                Country = current.Country,
                Description = current.Description,
                Entities = [.. current.Entities],
                Status = current.Status
            };

        if (incoming is null)
        {
            return result;
        }

        if (!string.IsNullOrWhiteSpace(incoming.ArrangementId)) result.ArrangementId = incoming.ArrangementId.Trim();
        if (!string.IsNullOrWhiteSpace(incoming.Country)) result.Country = incoming.Country.Trim();
        if (!string.IsNullOrWhiteSpace(incoming.Description)) result.Description = incoming.Description.Trim();
        if (incoming.Entities.Count > 0) result.Entities = incoming.Entities.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.Status = "draft";

        return result;
    }
}

public static class DraftFieldUpdater
{
    public static void ApplyUserAnswer(MdrDraft draft, string fieldName, string answer)
    {
        var cleaned = answer.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        switch (fieldName)
        {
            case nameof(MdrDraft.ArrangementId):
                draft.ArrangementId = cleaned;
                break;
            case nameof(MdrDraft.Country):
                draft.Country = cleaned;
                break;
            case nameof(MdrDraft.Description):
                draft.Description = cleaned;
                break;
            case nameof(MdrDraft.Entities):
                draft.Entities = cleaned
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                break;
        }
    }
}

public interface ISessionStore
{
    Task<DraftSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task UpsertSessionAsync(DraftSession session, CancellationToken cancellationToken);
    Task SaveMessageAsync(string sessionId, string role, string content, CancellationToken cancellationToken);
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, DraftSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Task<DraftSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session);
        }
    }

    public Task UpsertSessionAsync(DraftSession session, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _sessions[session.SessionId] = session;
            return Task.CompletedTask;
        }
    }

    public Task SaveMessageAsync(string sessionId, string role, string content, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class SqlSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _connectionString;

    public SqlSessionStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID('dbo.Sessions', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.Sessions (
                    SessionId NVARCHAR(64) NOT NULL PRIMARY KEY,
                    UserId NVARCHAR(128) NOT NULL,
                    Status NVARCHAR(32) NOT NULL,
                    CompletionState NVARCHAR(32) NOT NULL,
                    CurrentStep NVARCHAR(32) NOT NULL,
                    InputMode NVARCHAR(16) NOT NULL,
                    RequiresValidation BIT NOT NULL,
                    SourceFileName NVARCHAR(260) NULL,
                    SourceFileReference NVARCHAR(1024) NULL,
                    PendingField NVARCHAR(64) NULL,
                    DraftJson NVARCHAR(MAX) NOT NULL,
                    SkippedFieldsJson NVARCHAR(MAX) NOT NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL,
                    UpdatedAtUtc DATETIMEOFFSET NOT NULL
                );
            END;

            IF COL_LENGTH('dbo.Sessions', 'InputMode') IS NULL
                ALTER TABLE dbo.Sessions ADD InputMode NVARCHAR(16) NOT NULL CONSTRAINT DF_Sessions_InputMode DEFAULT 'Text';
            IF COL_LENGTH('dbo.Sessions', 'RequiresValidation') IS NULL
                ALTER TABLE dbo.Sessions ADD RequiresValidation BIT NOT NULL CONSTRAINT DF_Sessions_RequiresValidation DEFAULT 0;
            IF COL_LENGTH('dbo.Sessions', 'SourceFileName') IS NULL
                ALTER TABLE dbo.Sessions ADD SourceFileName NVARCHAR(260) NULL;
            IF COL_LENGTH('dbo.Sessions', 'SourceFileReference') IS NULL
                ALTER TABLE dbo.Sessions ADD SourceFileReference NVARCHAR(1024) NULL;

            IF OBJECT_ID('dbo.Messages', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.Messages (
                    MessageId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    SessionId NVARCHAR(64) NOT NULL,
                    Role NVARCHAR(32) NOT NULL,
                    Content NVARCHAR(MAX) NOT NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL,
                    INDEX IX_Messages_SessionId (SessionId)
                );
            END;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DraftSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        const string sql = """
             SELECT SessionId, UserId, Status, CompletionState, CurrentStep,
                 InputMode, RequiresValidation, SourceFileName, SourceFileReference,
                 PendingField, DraftJson, SkippedFieldsJson, CreatedAtUtc, UpdatedAtUtc
            FROM dbo.Sessions
            WHERE SessionId = @SessionId;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SessionId", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var draftJson = reader.GetString(10);
        var skippedJson = reader.GetString(11);

        return new DraftSession
        {
            SessionId = reader.GetString(0),
            UserId = reader.GetString(1),
            Status = Enum.Parse<DraftStatus>(reader.GetString(2), ignoreCase: true),
            CompletionState = reader.GetString(3),
            CurrentStep = reader.GetString(4),
            InputMode = Enum.Parse<InputMode>(reader.GetString(5), ignoreCase: true),
            RequiresValidation = reader.GetBoolean(6),
            SourceFileName = reader.IsDBNull(7) ? null : reader.GetString(7),
            SourceFileReference = reader.IsDBNull(8) ? null : reader.GetString(8),
            PendingField = reader.IsDBNull(9) ? null : reader.GetString(9),
            Draft = JsonSerializer.Deserialize<MdrDraft>(draftJson, JsonOptions) ?? new MdrDraft(),
            SkippedFields = JsonSerializer.Deserialize<HashSet<string>>(skippedJson, JsonOptions) ?? [],
            CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(12),
            UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(13)
        };
    }

    public async Task UpsertSessionAsync(DraftSession session, CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE dbo.Sessions AS target
            USING (SELECT @SessionId AS SessionId) AS source
            ON (target.SessionId = source.SessionId)
            WHEN MATCHED THEN
                UPDATE SET
                    UserId = @UserId,
                    Status = @Status,
                    CompletionState = @CompletionState,
                    CurrentStep = @CurrentStep,
                    InputMode = @InputMode,
                    RequiresValidation = @RequiresValidation,
                    SourceFileName = @SourceFileName,
                    SourceFileReference = @SourceFileReference,
                    PendingField = @PendingField,
                    DraftJson = @DraftJson,
                    SkippedFieldsJson = @SkippedFieldsJson,
                    UpdatedAtUtc = @UpdatedAtUtc
            WHEN NOT MATCHED THEN
                INSERT (SessionId, UserId, Status, CompletionState, CurrentStep, InputMode, RequiresValidation, SourceFileName, SourceFileReference, PendingField, DraftJson, SkippedFieldsJson, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@SessionId, @UserId, @Status, @CompletionState, @CurrentStep, @InputMode, @RequiresValidation, @SourceFileName, @SourceFileReference, @PendingField, @DraftJson, @SkippedFieldsJson, @CreatedAtUtc, @UpdatedAtUtc);
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SessionId", session.SessionId);
        cmd.Parameters.AddWithValue("@UserId", session.UserId);
        cmd.Parameters.AddWithValue("@Status", session.Status.ToString());
        cmd.Parameters.AddWithValue("@CompletionState", session.CompletionState);
        cmd.Parameters.AddWithValue("@CurrentStep", session.CurrentStep);
        cmd.Parameters.AddWithValue("@InputMode", session.InputMode.ToString());
        cmd.Parameters.AddWithValue("@RequiresValidation", session.RequiresValidation);
        cmd.Parameters.AddWithValue("@SourceFileName", (object?)session.SourceFileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SourceFileReference", (object?)session.SourceFileReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PendingField", (object?)session.PendingField ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DraftJson", JsonSerializer.Serialize(session.Draft, JsonOptions));
        cmd.Parameters.AddWithValue("@SkippedFieldsJson", JsonSerializer.Serialize(session.SkippedFields, JsonOptions));
        cmd.Parameters.AddWithValue("@CreatedAtUtc", session.CreatedAtUtc);
        cmd.Parameters.AddWithValue("@UpdatedAtUtc", session.UpdatedAtUtc);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveMessageAsync(string sessionId, string role, string content, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.Messages (SessionId, Role, Content, CreatedAtUtc)
            VALUES (@SessionId, @Role, @Content, @CreatedAtUtc);
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SessionId", sessionId);
        cmd.Parameters.AddWithValue("@Role", role);
        cmd.Parameters.AddWithValue("@Content", content);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", DateTimeOffset.UtcNow);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class FoundrySettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string AgentName { get; set; } = "mdr-text-extraction-agent";
}
