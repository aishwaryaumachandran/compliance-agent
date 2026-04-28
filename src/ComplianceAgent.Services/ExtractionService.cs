using System.Text.Json;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.AI.Extensions.OpenAI;
using Azure.Identity;
using OpenAI.Files;
using OpenAI.Responses;

namespace ComplianceAgent.Services;

public class ExtractionService
{
    private readonly FoundrySettings _settings;

    public ExtractionService(FoundrySettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<ExtractionResult> ExtractAsync(
        string inputFilePath,
        string agentInstructions,
        string extractionPrompt)
    {
        AIProjectClient projectClient = new(
            new Uri(_settings.Endpoint),
            new DefaultAzureCredential());

        // Upload the PDF via the Files API so Code Interpreter can access it
        Console.WriteLine("Uploading file to Foundry...");
        OpenAIFileClient fileClient = projectClient.ProjectOpenAIClient.GetOpenAIFileClient();
        OpenAIFile uploadedFile = fileClient.UploadFile(
            filePath: inputFilePath,
            purpose: FileUploadPurpose.Assistants);
        Console.WriteLine($"Uploaded file ID: {uploadedFile.Id}");

        // Create agent with Code Interpreter referencing the uploaded file
        DeclarativeAgentDefinition agentDefinition = new(model: _settings.Model)
        {
            Instructions = agentInstructions,
            Tools =
            {
                ResponseTool.CreateCodeInterpreterTool(
                    new CodeInterpreterToolContainer(
                        CodeInterpreterToolContainerConfiguration.CreateAutomaticContainerConfiguration(
                            fileIds: [uploadedFile.Id]
                        )
                    )
                ),
            }
        };

        ProjectsAgentVersion agentVersion = projectClient.AgentAdministrationClient.CreateAgentVersion(
            agentName: _settings.AgentName,
            options: new(agentDefinition));

        Console.WriteLine($"Agent created: {agentVersion.Name} v{agentVersion.Version}");

        try
        {
            // Send the prompt to the agent
            AgentReference agentReference = new(name: agentVersion.Name, version: agentVersion.Version);
            ProjectResponsesClient responsesClient = projectClient.ProjectOpenAIClient
                .GetProjectResponsesClientForAgent(agentReference);

            Console.WriteLine("Running extraction agent...");
            ResponseResult response = responsesClient.CreateResponse(extractionPrompt);
            var responseText = response.GetOutputText()?.Trim() ?? string.Empty;

            // Strip markdown code fences if present
            if (responseText.StartsWith("```"))
            {
                var firstNewline = responseText.IndexOf('\n');
                var lastFence = responseText.LastIndexOf("```");
                if (firstNewline > 0 && lastFence > firstNewline)
                {
                    responseText = responseText[(firstNewline + 1)..lastFence].Trim();
                }
            }

            // Validate and format the JSON
            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(responseText);
                var formattedJson = JsonSerializer.Serialize(parsed,
                    new JsonSerializerOptions { WriteIndented = true });

                return new ExtractionResult { Success = true, Json = formattedJson };
            }
            catch (JsonException ex)
            {
                return new ExtractionResult
                {
                    Success = false,
                    Json = responseText,
                    Error = $"Agent response was not valid JSON: {ex.Message}"
                };
            }
        }
        finally
        {
            // Clean up agent version
            projectClient.AgentAdministrationClient.DeleteAgentVersion(
                agentName: agentVersion.Name,
                agentVersion: agentVersion.Version);
        }
    }
}
