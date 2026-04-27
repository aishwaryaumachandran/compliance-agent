using System.Text;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

const string AgentInstructions = """
    You are a compliance assistant.
    Use the Code Interpreter tool to inspect file content and summarize key points.
    """;

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
if (string.IsNullOrWhiteSpace(settings.InputFile))
    throw new InvalidOperationException("InputFile is required in appsettings.json.");

var inputFile = ResolveInputFilePath(settings.InputFile, Path.GetDirectoryName(settingsPath)!)
    ?? throw new FileNotFoundException($"Input file not found: {settings.InputFile}");

var fileText = await File.ReadAllTextAsync(inputFile);

AIProjectClient projectClient = new(
    new Uri(settings.Foundry.Endpoint),
    new DefaultAzureCredential());

AIAgent agent = projectClient.AsAIAgent(
    model: settings.Foundry.Model,
    name: settings.Foundry.AgentName,
    instructions: AgentInstructions,
    tools: [new HostedCodeInterpreterTool() { Inputs = [] }]);

string prompt = $$"""
    Read this file content and provide a short summary:

    File name: {{Path.GetFileName(inputFile)}}
    ```
    {{fileText}}
    ```
    """;

AgentResponse response = await agent.RunAsync(prompt);

Console.WriteLine("Assistant response:");
Console.WriteLine(response.Text);

CodeInterpreterToolCallContent? toolCall = response.Messages
    .SelectMany(m => m.Contents)
    .OfType<CodeInterpreterToolCallContent>()
    .FirstOrDefault();

if (toolCall?.Inputs is not null)
{
    DataContent? codeInput = toolCall.Inputs.OfType<DataContent>().FirstOrDefault();
    if (codeInput?.HasTopLevelMediaType("text") ?? false)
    {
        Console.WriteLine();
        Console.WriteLine("Code Interpreter input:");
        Console.WriteLine(Encoding.UTF8.GetString(codeInput.Data.ToArray()));
    }
}

CodeInterpreterToolResultContent? toolResult = response.Messages
    .SelectMany(m => m.Contents)
    .OfType<CodeInterpreterToolResultContent>()
    .FirstOrDefault();

if (toolResult?.Outputs?.OfType<TextContent>().FirstOrDefault() is { } output)
{
    Console.WriteLine();
    Console.WriteLine("Code Interpreter result:");
    Console.WriteLine(output.Text);
}

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

public class BackendSettings
{
    public FoundrySettings Foundry { get; set; } = new();
    public string InputFile { get; set; } = "data\\Output.json";
}

public class FoundrySettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";
    public string AgentName { get; set; } = "compliance-agent-backend";
}
