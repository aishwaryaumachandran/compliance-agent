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
3. Optional: set `Foundry.PromptVersion` (default `v1`) to select the prompt template version.
3. Optional: set `ConnectionStrings:Drafts` (or `Storage:ConnectionString`) to persist sessions/messages in Azure SQL.

## Run

```powershell
dotnet run --project .\src\ComplianceAgent.Backend\ComplianceAgent.Backend.csproj
```

The API exposes:

1. `POST /draft/create` (unified text ingestion contract)
2. `POST /draft/from-file` (multipart upload)
3. `POST /draft/validate` (file-flow validation gate)
4. `POST /draft/from-text` (text ingestion, backward-compatible)
5. `POST /draft/respond` (clarification answer/skip)
6. `POST /draft/hold` (explicit hold state)
7. `POST /draft/confirm` (final confirmation)
8. `GET /draft/session/{sessionId}` (session state)
9. `GET /prompt-library` (active prompt version + available versions)

## Unified Input Contract

Use `POST /draft/create` for text ingestion with a common request shape:

```json
{
	"userId": "user-123",
	"sessionId": "optional",
	"inputMode": "Text",
	"inputText": "Arrangement in Germany between Alpha GmbH and Beta SARL requiring disclosure."
}
```

For file ingestion use `POST /draft/from-file` (multipart form data) with `userId`, optional `sessionId`, and `file`.

## File Validation Loop

File ingestion now returns `currentStep: "validation"` and `requiresValidation: true`.
Before clarification can continue, call `POST /draft/validate`:

```json
{
	"sessionId": "<session-id>",
	"accepted": true,
	"corrections": {
		"arrangementId": null,
		"country": "Germany",
		"description": "Optional correction",
		"entities": ["Alpha GmbH", "Beta SARL"],
		"transactionType": null,
		"status": "draft"
	}
}
```

If `accepted` is `false`, the draft moves to hold (`status: "Hold"`, `completionState: "hold"`).

## Lifecycle States

`status` is serialized as string values:

1. `Started`
2. `InProgress`
3. `Hold`
4. `Review`
5. `Completed`

`completionState` values used in responses include `partial`, `ready_for_review`, `hold`, and `completed`.

## Original Endpoints

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

## Prompt Library Tests

Run prompt-library and parser tests:

```powershell
dotnet test .\tests\ComplianceAgent.Backend.Tests\ComplianceAgent.Backend.Tests.csproj
```
