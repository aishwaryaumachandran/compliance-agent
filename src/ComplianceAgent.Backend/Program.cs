using System.Text.Json;
using ComplianceAgent.Services;

const string DefaultAgentInstructions = """
You are a regulatory compliance extraction agent.

Rules:
- Read the uploaded document using Code Interpreter.
- Return ONLY valid JSON.
- Do not include markdown fences.
- If a value is missing, set it to null (or [] for arrays).
- Do not infer or invent values.
""";

const string DefaultExtractionPrompt = """
Extract structured arrangement data from the uploaded file '{{input_file}}'.

Return ONLY JSON using this exact shape:
{
    "arrangementId": null,
    "country": null,
    "entities": [],
    "description": null,
    "transactionType": null,
    "status": "draft"
}

Do not add extra fields.
""";

// --- Parse CLI arguments ---
string? fileInputArg = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--file-input" && i + 1 < args.Length)
    {
        fileInputArg = args[++i];
    }
}

// --- Load settings ---
const string SettingsFileName = "appsettings.json";
var settingsPath = ResolveSettingsPath(SettingsFileName)
    ?? throw new FileNotFoundException(
        $"Could not find '{SettingsFileName}'. Place it in repo root or in 'src\\ComplianceAgent.Backend'.");

if (!string.IsNullOrWhiteSpace(sqlConnectionString))
{
    PropertyNameCaseInsensitive = true
}) ?? throw new InvalidOperationException("Could not parse appsettings.json.");

if (string.IsNullOrWhiteSpace(settings.Foundry.Endpoint))
    throw new InvalidOperationException("Foundry:Endpoint is required in appsettings.json.");
if (string.IsNullOrWhiteSpace(settings.Foundry.Model))
    throw new InvalidOperationException("Foundry:Model is required in appsettings.json.");

// CLI --file-input overrides appsettings.json InputFile
var configuredInput = fileInputArg ?? settings.InputFile;
if (string.IsNullOrWhiteSpace(configuredInput))
    throw new InvalidOperationException("No input file specified. Use --file-input <path> or set InputFile in appsettings.json.");

var inputFile = ResolveInputFilePath(configuredInput, Path.GetDirectoryName(settingsPath)!)
    ?? throw new FileNotFoundException($"Input file not found: {configuredInput}");

Console.WriteLine($"Processing: {Path.GetFileName(inputFile)}");

// --- Load prompts ---
var agentInstructions = await LoadPromptTextAsync(
    "ExtractionAgentInstructions.txt",
    DefaultAgentInstructions);

var extractionPromptTemplate = await LoadPromptTextAsync(
    "ExtractionPrompt.txt",
    DefaultExtractionPrompt);

var extractionPrompt = extractionPromptTemplate.Replace("{{input_file}}", Path.GetFileName(inputFile));
extractionPrompt = extractionPrompt.Replace("{{file_name}}", Path.GetFileName(inputFile));

// --- Run extraction ---
var foundrySettings = new FoundrySettings
{
    Endpoint = settings.Foundry.Endpoint,
    Model = settings.Foundry.Model,
    AgentName = settings.Foundry.AgentName
};

var extractionService = new ExtractionService(foundrySettings);
var result = await extractionService.ExtractAsync(inputFile, agentInstructions, extractionPrompt);

if (result.Success)
{
    var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "extractedjson");
    Directory.CreateDirectory(outputDir);
    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var outputFileName = $"{Path.GetFileNameWithoutExtension(inputFile)}_extracted_{timestamp}.json";
    var outputPath = Path.Combine(outputDir, outputFileName);
    await File.WriteAllTextAsync(outputPath, result.Json);

    Console.WriteLine($"Extraction complete. Output saved to: {outputPath}");
    Console.WriteLine();
    Console.WriteLine(result.Json);
}
else
{
    Console.WriteLine($"Warning: {result.Error}");
    Console.WriteLine("Raw output:");
    Console.WriteLine(result.Json);
}

// --- Helpers ---

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
                    PendingField NVARCHAR(64) NULL,
                    DraftJson NVARCHAR(MAX) NOT NULL,
                    SkippedFieldsJson NVARCHAR(MAX) NOT NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL,
                    UpdatedAtUtc DATETIMEOFFSET NOT NULL
                );
            END;

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
            SELECT SessionId, UserId, Status, CompletionState, CurrentStep, PendingField,
                   DraftJson, SkippedFieldsJson, CreatedAtUtc, UpdatedAtUtc
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

        var draftJson = reader.GetString(6);
        var skippedJson = reader.GetString(7);

        return new DraftSession
        {
            SessionId = reader.GetString(0),
            UserId = reader.GetString(1),
            Status = Enum.Parse<DraftStatus>(reader.GetString(2), ignoreCase: true),
            CompletionState = reader.GetString(3),
            CurrentStep = reader.GetString(4),
            PendingField = reader.IsDBNull(5) ? null : reader.GetString(5),
            Draft = JsonSerializer.Deserialize<MdrDraft>(draftJson, JsonOptions) ?? new MdrDraft(),
            SkippedFields = JsonSerializer.Deserialize<HashSet<string>>(skippedJson, JsonOptions) ?? [],
            CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(8),
            UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(9)
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
                    PendingField = @PendingField,
                    DraftJson = @DraftJson,
                    SkippedFieldsJson = @SkippedFieldsJson,
                    UpdatedAtUtc = @UpdatedAtUtc
            WHEN NOT MATCHED THEN
                INSERT (SessionId, UserId, Status, CompletionState, CurrentStep, PendingField, DraftJson, SkippedFieldsJson, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@SessionId, @UserId, @Status, @CompletionState, @CurrentStep, @PendingField, @DraftJson, @SkippedFieldsJson, @CreatedAtUtc, @UpdatedAtUtc);
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SessionId", session.SessionId);
        cmd.Parameters.AddWithValue("@UserId", session.UserId);
        cmd.Parameters.AddWithValue("@Status", session.Status.ToString());
        cmd.Parameters.AddWithValue("@CompletionState", session.CompletionState);
        cmd.Parameters.AddWithValue("@CurrentStep", session.CurrentStep);
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

static string? ResolvePromptsDirectory()
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "src", "prompts"),
        Path.Combine(Directory.GetCurrentDirectory(), "src", "ComplianceAgent.Backend", "prompts"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "prompts")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "ComplianceAgent.Backend", "prompts")),
        Path.Combine(AppContext.BaseDirectory, "prompts")
    };

    return candidates.FirstOrDefault(Directory.Exists);
}

static async Task<string> LoadPromptTextAsync(string fileName, string fallback)
{
    var promptsDir = ResolvePromptsDirectory();
    if (!string.IsNullOrWhiteSpace(promptsDir))
    {
        var filePath = Path.Combine(promptsDir, fileName);
        if (File.Exists(filePath))
        {
            return await File.ReadAllTextAsync(filePath);
        }
    }

    Console.WriteLine($"Prompt file '{fileName}' not found. Using built-in default template.");
    return fallback;
}

public class BackendSettings
{
    public FoundrySettings Foundry { get; set; } = new();
    public string InputFile { get; set; } = "data\\Output.json";
}
