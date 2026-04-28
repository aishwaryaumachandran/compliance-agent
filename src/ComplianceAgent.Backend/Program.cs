using System.Text.Json;
using ComplianceAgent.Services;

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
var promptsDir = ResolvePromptsDirectory()
    ?? throw new DirectoryNotFoundException("Could not find 'src/prompts' directory.");
var agentInstructions = await File.ReadAllTextAsync(
    Path.Combine(promptsDir, "ExtractionAgentInstructions.txt"));
var extractionPrompt = await File.ReadAllTextAsync(
    Path.Combine(promptsDir, "ExtractionPrompt.txt"));

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

static string? ResolveSettingsPath(string fileName)
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), fileName),
        Path.Combine(Directory.GetCurrentDirectory(), "src", "ComplianceAgent.Backend", fileName),
        Path.Combine(AppContext.BaseDirectory, fileName),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", fileName))
    };

    return candidates.FirstOrDefault(File.Exists);
}

static string? ResolveInputFilePath(string configuredPath, string settingsDirectory)
{
    if (Path.IsPathRooted(configuredPath))
    {
        return File.Exists(configuredPath) ? configuredPath : null;
    }

    var candidates = new[]
    {
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredPath)),
        Path.GetFullPath(Path.Combine(settingsDirectory, configuredPath))
    };

    return candidates.FirstOrDefault(File.Exists);
}

static string? ResolvePromptsDirectory()
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "src", "prompts"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "prompts")),
        Path.Combine(AppContext.BaseDirectory, "prompts")
    };

    return candidates.FirstOrDefault(Directory.Exists);
}

public class BackendSettings
{
    public FoundrySettings Foundry { get; set; } = new();
    public string InputFile { get; set; } = "data\\Output.json";
}
