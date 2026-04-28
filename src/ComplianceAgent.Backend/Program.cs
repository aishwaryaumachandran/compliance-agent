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

var json = await File.ReadAllTextAsync(settingsPath);
var settings = JsonSerializer.Deserialize<BackendSettings>(json, new JsonSerializerOptions
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<IMissingFieldService, MissingFieldService>();
builder.Services.AddSingleton<IAiExtractionService, FoundryExtractionService>();
builder.Services.AddSingleton<IFileTextExtractor, FileTextExtractor>();

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
