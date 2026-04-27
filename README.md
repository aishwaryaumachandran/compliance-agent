# Compliance Agent Backend

Minimal backend service under `src\ComplianceAgent.Backend` that builds MDR drafts from free-form text with a clarification loop before final confirmation.

## Prerequisites

- .NET 10 SDK
- Azure AI Foundry project + model deployment
- Microsoft Entra access (run `az login`)
- Optional: Azure SQL connection string for persistent sessions

## Setup

1. Copy `src\ComplianceAgent.Backend\appsettings.template.json` to `src\ComplianceAgent.Backend\appsettings.json`.
2. Set `Foundry.Endpoint` and `Foundry.Model`.
3. Optional: set `ConnectionStrings:Drafts` (or `Storage:ConnectionString`) to persist sessions/messages in Azure SQL.

## Run

```powershell
dotnet run --project .\src\ComplianceAgent.Backend\ComplianceAgent.Backend.csproj
```

The API exposes:

1. `POST /draft/from-text`
2. `POST /draft/respond`
3. `POST /draft/confirm`

## Flow Overview

1. Submit free text to `/draft/from-text`.
2. The service extracts known fields only and leaves unknown fields null/empty.
3. If required fields are missing, it returns a follow-up question (`pendingField`, `nextQuestion`).
4. Reply via `/draft/respond` with either `answer` or `skip=true`.
5. Confirm the review-ready draft using `/draft/confirm`.

## Example Calls

```powershell
$fromText = @{
	userId = "user-123"
	inputText = "Arrangement in Germany between Alpha GmbH and Beta SARL requiring disclosure."
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://127.0.0.1:5187/draft/from-text" -Method Post -ContentType "application/json" -Body $fromText
```

```powershell
$respond = @{
	sessionId = "<session-id-from-previous-response>"
	answer = "MDR-2026-0007"
	skip = $false
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://127.0.0.1:5187/draft/respond" -Method Post -ContentType "application/json" -Body $respond
```

```powershell
$confirm = @{ sessionId = "<session-id-from-previous-response>" } | ConvertTo-Json

Invoke-RestMethod -Uri "http://127.0.0.1:5187/draft/confirm" -Method Post -ContentType "application/json" -Body $confirm
```

## Smoke Test

Run a one-command verification that covers:

1. Health endpoint check
2. Answer-path flow (`/draft/from-text` -> `/draft/respond` -> `/draft/confirm`)
3. Skip-path flow (`/draft/from-text` -> repeated `/draft/respond` with `skip=true` -> `/draft/confirm`)

From repo root:

```powershell
.\scripts\manual_smoke.ps1
```

Use a custom API base URL:

```powershell
.\scripts\manual_smoke.ps1 -BaseUrl "http://127.0.0.1:5190"
```
