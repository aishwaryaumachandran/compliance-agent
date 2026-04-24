# Compliance Intelligence Agent – Technical Design

**Date:** April 16, 2026  
**Scope:** Phase 1 – Single document → Single compliance case draft  

---

## 1. System Overview

A multi-agent system that ingests unstructured compliance-related inputs (documents, text snippets) and produces structured compliance case drafts via a human-in-the-loop conversational flow.

### Three Core Features

| # | Feature | Input | Output |
|---|---|---|---|
| F1 | **Compliance Q&A Chat (RAG)** | User question about compliance regulations | Grounded answer from knowledge base |
| F2 | **Document Upload → Case Draft** | PDF / Word file | Structured case JSON |
| F3 | **Text Prompt → Case Draft** | Free-text description (paste/type) | Structured case JSON (with follow-up prompting for missing fields) |

---

## 2. Technology Stack

### Target State

| Layer | Technology | Notes |
|---|---|---|
| **LLM** | **Azure OpenAI – GPT-5.2** | Stronger reasoning for compliance classification, better structured output adherence |
| **Embeddings** | text-embedding-3-small | Sufficient for compliance knowledge base retrieval |
| **Agent Framework** | **Microsoft Agent Framework (latest)** | Multi-agent orchestration, tool calling, structured output, human-in-the-loop patterns |
| **Document Processing** | **Azure AI Content Understanding** *(optional)* | Unified multi-modal extraction from PDF/Word — text, tables, images, diagrams in a single pipeline. If not used, documents are parsed via GPT-5.2 vision directly. |
| **Storage** | Azure Blob Storage | Uploaded documents, knowledge base source files |
| **Database** | Azure SQL Database | FAQ Q&A pairs (with vector search), sessions, case drafts, audit log |
| **Backend API** | .NET 8 ASP.NET Core | Hosts agent endpoints, orchestration |
| **Frontend** | Simple chat UI (Angular) | File upload, chat, case draft display — minimal |
| **Monitoring** | Application Insights | Telemetry, prompt logging, latency tracking |

---

## 4. Agent Architecture

### 4.1 Agent Topology

Two agents coordinated via Microsoft Agent Framework:

```
┌─────────────────────────────────────────────────────────┐
│                    ORCHESTRATOR                          │
│              (Microsoft Agent Framework)                 │
│                                                         │
│  ┌─────────────────┐       ┌──────────────────────┐    │
│  │   CHAT AGENT    │       │  EXTRACTION AGENT     │    │
│  │                 │       │                       │    │
│  │ - RAG Q&A       │  ───▶ │ - Document parsing    │    │
│  │ - Conversation  │       │ - Field extraction    │    │
│  │ - Follow-up Qs  │  ◀─── │ - Classification     │    │
│  │ - Guardrails    │       │ - JSON output         │    │
│  └─────────────────┘       └──────────────────────┘    │
│           │                          │                   │
│           ▼                          ▼                   │
│  ┌─────────────────┐       ┌──────────────────────┐    │
│  │     TOOLS       │       │       TOOLS           │    │
│  │ - SQL vector    │       │ - Content Understand. │    │
│  │   search (FAQ)  │       │   (optional)          │    │
│  │ - Session store │       │ - Schema validator    │    │
│  │ - Off-topic     │       │ - Country validator   │    │
│  │   detector      │       │ - Entity lookup       │    │
│  └─────────────────┘       └──────────────────────┘    │
└─────────────────────────────────────────────────────────┘
```

### 4.2 Chat Agent

**Role:** User-facing conversational agent. Handles all three features.

| Responsibility | Detail |
|---|---|
| **Route intent** | Determine if user wants Q&A, document upload, or text-based case creation |
| **RAG Q&A** | Query Azure SQL DB (FAQ vector search) for compliance knowledge; synthesize grounded answer via GPT-5.2 |
| **Conversation management** | Multi-turn context, follow-up questions for missing fields, user confirmation |
| **Guardrails** | Detect off-topic input; terminate with polite message |
| **Invoke Extraction Agent** | Delegate document/text analysis to the Extraction Agent as a tool call |
| **Present results** | Show extracted fields, ask for confirmation, create case draft |

### 4.3 Extraction Agent

**Role:** Specialist agent for case field extraction and classification reasoning.

| Responsibility | Detail |
|---|---|
| **Document ingestion** | Accept parsed document content (from Content Understanding if enabled, or directly via GPT-5.2 vision) or raw text input |
| **Field extraction** | Extract all case fields into the output JSON schema |
| **Classification reasoning** | Map case facts to compliance classification codes with chain-of-thought reasoning |
| **Cross-jurisdiction assessment** | Evaluate multi-jurisdiction criteria |
| **Jurisdiction validation** | Validate extracted jurisdictions against the valid jurisdiction list |
| **Missing field detection** | Return which required fields could not be extracted (triggers Chat Agent follow-up) |
| **Structured output** | Produce validated JSON conforming to the case schema |

---

## 5. Data Flow – Feature by Feature

### 5.1 Feature 1: Compliance Q&A Chat (RAG)

```
User Question
    │
    ▼
Chat Agent
    │
    ├── Off-topic? ──▶ Terminate conversation
    │
    ├── Query Azure SQL DB (FAQ vector search over compliance knowledge)
    │
    ├── GPT-5.2: Synthesize answer grounded in search results
    │
    ▼
Grounded Answer → User
```

### 5.2 Feature 2: Document Upload → Case Draft

```
User uploads PDF/Word
    │
    ▼
Azure Blob Storage (temp)
    │
    ▼
[If Content Understanding enabled]
Azure AI Content Understanding
    ├── Extract text, tables, layout, images (unified)
[Else]
GPT-5.2 vision: direct document analysis
    │
    ▼
Extraction Agent (GPT-5.2)
    ├── Extract case fields
    ├── Identify classification codes (with reasoning)
    ├── Validate jurisdictions
    ├── Detect missing required fields
    │
    ▼
Chat Agent
    ├── Present extracted fields to user
    ├── "I found [fields]. Missing: [trigger date, intermediaries]. Create draft?"
    │
    ▼
User confirms / provides missing data
    │
    ▼
Final JSON → Schema Validation → Case UI
```

### 5.3 Feature 3: Text Prompt → Case Draft (Human-in-the-Loop)

```
User pastes text description
    │
    ▼
Chat Agent → Extraction Agent (GPT-5.2)
    ├── Extract whatever fields are present
    ├── Identify classification codes
    ├── Return missing required fields list
    │
    ▼
Chat Agent
    ├── "Here's what I extracted: [summary]"
    ├── "Missing: trigger date, intermediaries. Can you provide these?"
    │
    ▼
User provides answers (or skips)
    │
    ▼ (loop until user says "create" or "proceed")
    │
Chat Agent → Extraction Agent (merge new info)
    │
    ▼
Final JSON → Schema Validation → Case UI
```

---

## 6. Key Components – Design Detail

### 6.1 Document Processing Pipeline (Content Understanding – Optional)

Content Understanding is an optional enhancement. Without it, documents are uploaded to Blob Storage and processed directly by GPT-5.2 vision. With it, a richer extraction pipeline is available:

```
Input File (PDF/Word/PPTX)
    │
    ▼
┌──────────────────────────────────────────────────────┐
│  Option A: Azure AI Content Understanding      │
│  ├── Text content (paragraphs, headings)        │
│  ├── Tables (structured rows/columns)           │
│  ├── Images & diagrams (analyzed in-pipeline)   │
│  ├── Layout & spatial relationships             │
│  └── → Unified Markdown representation           │
├──────────────────────────────────────────────────────┤
│  Option B: Direct GPT-5.2 Vision               │
│  ├── Upload file to Blob Storage                │
│  └── Pass to GPT-5.2 with vision capabilities   │
└──────────────────────────────────────────────────────┘
    │
    ▼
Extraction Agent prompt
```

**When to use Content Understanding:**
- Documents with complex tables, multi-column layouts, or embedded diagrams
- High-fidelity extraction needed (preserves spatial relationships)
- PowerPoint support required

**When direct GPT-5.2 vision is sufficient:**
- Simple text-heavy documents (PDFs, Word files)
- Lower latency / fewer Azure services to manage
- Faster initial setup during development

### 6.2 RAG – Compliance Knowledge Base

| Component | Detail |
|---|---|
| **Index source** | Regulatory directive text, classification definitions, jurisdiction-specific rules, internal compliance guidance |
| **Chunking** | Semantic chunking by section/topic; ~500–800 tokens per chunk |
| **Embeddings** | text-embedding-3-small → stored as vectors in Azure SQL Database |
| **Retrieval** | Vector search over FAQ Q&A pairs in Azure SQL Database |
| **Grounding** | Search results injected into Chat Agent system prompt as context |

### 6.3 Classification Reasoning (Extraction Agent)

The Extraction Agent uses a structured prompt pattern for classification identification:

```
1. Analyze the case facts
2. For each compliance classification code, evaluate:
   - Does the case exhibit characteristics of this classification?
   - What evidence from the input supports or contradicts it?
   - Confidence: high / medium / low
3. For classifications requiring additional threshold tests:
   - Assess whether the relevant threshold criteria are met
4. Output: list of triggered classifications with reasoning
```

**Jurisdiction-specific rules** will be stored in the knowledge base and retrieved via RAG when the relevant jurisdiction is involved.

### 6.4 Off-Topic Guardrail

| Approach | Detail |
|---|---|
| **Primary** | System prompt instruction: "You are a regulatory compliance specialist. If the user asks about topics unrelated to compliance, politely decline and end the conversation." |
| **Secondary** | Intent classifier tool: lightweight GPT call that classifies user input as `compliance-related`, `ambiguous`, or `off-topic` before the main agent processes it |
| **Fallback** | Keyword/phrase blocklist for obvious off-topic (sports scores, recipes, etc.) |

### 6.5 Session & State Management

| Aspect | Design |
|---|---|
| **Chat history** | Stored in Azure SQL Database, keyed by session ID |
| **Session lifecycle** | Created on first message; persists across tab close; cleared on logout |
| **Case draft state** | Stored in Azure SQL Database as a versioned JSON record (updated as user provides more info) |
| **Conversation context window** | Last N messages + current case draft state passed to agent on each turn |

### 6.6 Output Schema Validation

```
Extraction Agent output
    │
    ▼
JSON Schema Validator (built-in to pipeline)
    │
    ├── Valid → pass to Case UI
    │
    ├── Invalid → log error + retry extraction with feedback
    │
    ▼
Case UI renders draft
```

GPT-5.2 structured output mode will be used to enforce JSON schema compliance at generation time, with a post-generation validation step as a safety net.

---

## 7. Azure Resource Topology

```
Resource Group: rg-compliance-agent
│
├── Azure OpenAI Service
│   ├── Deployment: gpt-5.2 (chat + vision)
│   └── Deployment: text-embedding-3-small
│
├── Azure AI Content Understanding (optional)
│   └── Multi-modal document analyzer
│
├── Azure SQL Database
│   ├── Table: faq-knowledge-base (with vector columns)
│   ├── Table: sessions
│   ├── Table: case-drafts
│   └── Table: audit-log
│
├── Azure Storage Account
│   ├── Container: uploaded-documents
│   └── Container: knowledge-base-source
│
├── Azure App Service
│   └── .NET 8 backend + agent orchestration
│
├── Azure Static Web App
│   └── Simple chat UI (Angular)
│
└── Application Insights
    └── Telemetry + prompt logging
```

---

## 8. API Design (Simplified)

| Endpoint | Method | Purpose |
|---|---|---|
| `POST /api/chat` | POST | Send a message; returns agent response (streaming) |
| `POST /api/upload` | POST | Upload a document; triggers extraction pipeline |
| `GET /api/session/{id}` | GET | Retrieve session history |
| `POST /api/session` | POST | Create new session |
| `DELETE /api/session/{id}` | DELETE | Clear session (logout) |
| `GET /api/case/{id}` | GET | Retrieve case draft |
| `PUT /api/case/{id}` | PUT | Update case draft (after user edits) |
| `POST /api/case/{id}/confirm` | POST | Finalize case draft → send to case UI |

---

## 9. Microsoft Agent Framework – Integration Pattern

### Agent Definition (Pseudocode)

```csharp
// Chat Agent - user-facing
var chatAgent = AgentBuilder.Create("Compliance-Chat-Agent")
    .WithModel("gpt-5.2")
    .WithSystemPrompt(chatSystemPrompt)
    .WithTools(
        sqlVectorSearchTool,    // RAG over FAQ Q&A pairs in Azure SQL DB
        extractionAgentTool,    // Invoke Extraction Agent
        sessionStoreTool,       // Read/write session state
        offTopicDetectorTool    // Intent classification
    )
    .WithStructuredOutput<ChatResponse>()
    .Build();

// Extraction Agent - specialist
var extractionAgent = AgentBuilder.Create("Compliance-Extraction-Agent")
    .WithModel("gpt-5.2")
    .WithSystemPrompt(extractionSystemPrompt)
    .WithTools(
        contentUnderstandingTool, // Parse documents (optional – multi-modal)
        jurisdictionValidatorTool, // Validate jurisdictions
        schemaValidatorTool,    // Validate output JSON
        entityLookupTool        // Check if entities exist in system
    )
    .WithStructuredOutput<CaseDraft>()
    .Build();
```

### Human-in-the-Loop Pattern

```csharp
// The Chat Agent manages the loop
while (!userConfirmed)
{
    // 1. Get extraction result (with missing fields list)
    var result = await extractionAgent.InvokeAsync(currentInput);
    
    // 2. Present to user, ask about missing fields
    var response = await chatAgent.InvokeAsync(
        $"Extracted: {result.Draft}. Missing: {result.MissingFields}. Ask user."
    );
    
    // 3. User responds with more info or says "proceed"
    var userInput = await GetUserInput();
    
    // 4. Merge new info into context
    currentInput = MergeContext(currentInput, userInput);
}

// 5. Validate and finalize
var finalDraft = await schemaValidator.Validate(result.Draft);
await sqlDb.SaveCaseDraft(finalDraft);
```

---

## 10. Provisioning Checklist

Azure resources to provision before development begins:

| # | Resource | SKU / Config | Notes |
|---|---|---|---|
| 1 | Azure OpenAI Service | GPT-5.2 deployment (chat + vision) | Ensure sufficient TPM quota for dev/test |
| 2 | Azure OpenAI Service | text-embedding-3-small deployment | For RAG indexing (matches current) |
| 3 | Azure AI Content Understanding | Standard tier | **Optional** — multi-modal document analysis. Can start without it using GPT-5.2 vision directly. |
| 4 | Azure SQL Database | Standard tier | Vector search enabled, sessions, case drafts, audit log |
| 5 | Azure Storage Account | Standard LRS | Two blob containers |
| 6 | Azure App Service | B1+ plan | .NET 8 runtime |
| 7 | Application Insights | Standard | Connected to App Service |
| 8 | Resource Group | `rg-compliance-agent` | All resources co-located |
| 9 | Azure AD / Entra ID | App registration | Auth for API + managed identity for service-to-service |

---

## 11. Key Design Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | **GPT-5.2 over GPT-4o-mini** | Stronger reasoning for compliance classification; better structured output compliance; native vision for document images |
| D2 | **Two-agent architecture (Chat + Extraction)** | Separation of concerns: Chat handles UX/conversation, Extraction handles specialist compliance logic. |
| D3 | **Azure AI Content Understanding for parsing (optional)** | Unified multi-modal pipeline for complex documents. Optional — simpler deployments can use GPT-5.2 vision directly. Add Content Understanding when document complexity warrants it. |
| D4 | **Azure SQL Database over in-memory cache** | Production-ready session persistence; survives restarts; supports multi-user concurrent access; consolidates FAQ vector search, sessions, case drafts, and audit log in one service |
| D5 | **Structured output mode** | GPT-5.2 structured output ensures JSON schema compliance at generation time, reducing post-processing failures |
| D6 | **SQL vector search for RAG** | FAQ Q&A pairs stored with vector embeddings in Azure SQL Database; avoids a separate search service; simpler architecture |
| D7 | **Simple UI** | Unblocks agent development; can be replaced later |
