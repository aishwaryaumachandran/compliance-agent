# Compliance Agent Backend

Minimal backend service starter under `src\ComplianceAgent.Backend` that creates an Azure AI Foundry agent with the **Code Interpreter** tool and analyzes file content.

## Prerequisites

- .NET 8 SDK
- Azure AI Foundry project + model deployment
- Microsoft Entra access (run `az login`)

## Setup

1. Copy `src\ComplianceAgent.Backend\appsettings.template.json` to `src\ComplianceAgent.Backend\appsettings.json`.
2. Set `Foundry.Endpoint` and `Foundry.Model`.
3. Set `InputFile` to the file you want to analyze (default: `data\Output.json`).

## Run

```powershell
dotnet run --project .\src\ComplianceAgent.Backend\ComplianceAgent.Backend.csproj
```

The app will:

1. read the file content
2. create a Foundry agent with `HostedCodeInterpreterTool`
3. ask the agent to summarize the file
4. print assistant output and code-interpreter tool output (when available)
