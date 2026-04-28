# Compliance Agent

An automated compliance extraction tool built on [Azure AI Foundry](https://learn.microsoft.com/en-us/azure/ai-studio/). It leverages a declarative agent with the **Code Interpreter** tool to read PDF documents describing arrangements and produce structured JSON output aligned to the reporting schema.

---

## Table of Contents

- [Architecture](#architecture)
- [Project Structure](#project-structure)
- [Prerequisites](#prerequisites)
- [Getting Started](#getting-started)
- [Usage](#usage)
- [Configuration](#configuration)
- [Key Dependencies](#key-dependencies)


**Flow:**
1. The CLI uploads the input PDF to Foundry via the Files API.
2. A declarative agent is created with Code Interpreter access to the uploaded file.
3. Extraction prompts are sent to the agent, which reads the PDF in a sandboxed environment.
4. The agent returns structured JSON, which is validated and saved locally.
5. The agent version is cleaned up automatically.

---

## Project Structure

```
compliance-agent/
├── src/
│   ├── ComplianceAgent.Backend/     # CLI entry point & configuration
│   │   ├── Program.cs               # Argument parsing, prompt loading, orchestration
│   │   ├── appsettings.json         # Runtime configuration (gitignored)
│   │   └── appsettings.template.json# Configuration template
│   ├── ComplianceAgent.Services/    # Service layer (Azure AI Foundry integration)
│   │   ├── ExtractionService.cs     # Agent creation, file upload, prompt execution
│   │   ├── ExtractionResult.cs      # Extraction result model
│   │   └── FoundrySettings.cs       # Foundry configuration model
│   └── prompts/                     # Externalised agent prompts
│       ├── ExtractionAgentInstructions.txt  # System instructions with JSON schema
│       └── ExtractionPrompt.txt             # User-facing extraction prompt
├── data/                            # Sample input files & reference schema
├── extractedjson/                   # Timestamped extraction output
├── docs/                            # Architecture & design documentation
├── compliance-agent.sln
└── README.md
```

---

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 8 SDK** | [Download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **Azure Subscription** | With access to Azure AI Foundry |
| **Azure AI Foundry Project** | A project with a deployed model (e.g. `gpt-5.2`) |
| **Microsoft Entra ID** | Authenticated via `az login` or managed identity |
| **Storage Account Access** | The Foundry-linked storage account must allow file uploads |

---

## Getting Started

### 1. Clone the repository

```bash
git clone <repository-url>
cd compliance-agent
```

### 2. Configure settings

```bash
cp src/ComplianceAgent.Backend/appsettings.template.json src/ComplianceAgent.Backend/appsettings.json
```

Edit `appsettings.json` with your Foundry project details:

```json
{
  "Foundry": {
    "Endpoint": "https://<your-foundry>.services.ai.azure.com/api/projects/<your-project>",
    "Model": "gpt-5.2",
    "AgentName": "compliance-agent-backend"
  },
  "InputFile": "data\\input1.pdf"
}
```

### 3. Authenticate

```bash
az login
```

### 4. Build

```bash
dotnet build compliance-agent.sln
```

---

## Usage

### Extract from a specific PDF

```powershell
dotnet run --project src/ComplianceAgent.Backend -- --file-input data/input1.pdf
```

### Use the default input file from configuration

```powershell
dotnet run --project src/ComplianceAgent.Backend
```

> The `--file-input` CLI argument takes precedence over the `InputFile` value in `appsettings.json`.

---

## Configuration

| Setting | Description | Default |
|---|---|---|
| `Foundry.Endpoint` | Azure AI Foundry project endpoint URL | *(required)* |
| `Foundry.Model` | Deployed model name | `gpt-4o-mini` |
| `Foundry.AgentName` | Agent name used for version management | `compliance-agent-backend` |
| `InputFile` | Default input file path (relative to repo root) | `data\input1.pdf` |

---

## Key Dependencies

| Package | Version | Purpose |
|---|---|---|
| `Azure.AI.Projects` | 2.0.0 | Foundry project client |
| `Azure.AI.Projects.Agents` | 2.0.0 | Declarative agent definitions & versioning |
| `Azure.AI.Extensions.OpenAI` | 2.0.0 | ProjectResponsesClient & agent references |
| `Azure.Identity` | 1.21.0 | Microsoft Entra ID authentication |
