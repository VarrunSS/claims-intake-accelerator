# Architecture

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](00-discovery.md).

This is the target architecture. Milestone 1 built the skeleton: the API, the legacy mock, the domain rules
and the infrastructure. Milestone 2 added the intake pipeline, the extractors, the review UI and the write-back
to the legacy system, and runs end to end on a laptop in a local mode ([ADR 0005](adr/0005-local-development-mode.md)).
Milestone 3 added the policy wording search, the agent tools, the coverage assessment and the audit log
([ADR 0006](adr/0006-policy-wording-rag.md)).
Milestone 5 added a model comparison and prompt versioning harness around the same extractor and assessor
([ADR 0008](adr/0008-model-selection-and-tuning.md)); it is an evaluation tool, not a runtime component, and has never been run
against a real model. Parts that are not built yet carry the milestone that adds them in their label. The table below the diagram
gives the status of each component, and the section after it shows how the two modes differ.

## Flow

```mermaid
flowchart LR
    subgraph Intake["Intake (Milestones 1-2, Azure path untested)"]
        direction TB
        Inbox[("Blob Storage<br/>inbox")]
        EG{{"Event Grid<br/>blob created"}}
        Fn["Azure Functions<br/>.NET 10 isolated"]
        DI["Document Intelligence<br/>OCR and layout<br/>(M2, untested)"]
        Inbox --> EG --> Fn --> DI
    end

    subgraph Agent["Extraction (M2) and assessment (M3)"]
        direction TB
        AG["IClaimExtractor (M2)<br/>IClaimAssessor (M3)<br/>rules locally, Agent Framework on Azure<br/>(Azure path untested)"]
        T1["lookup_policy"]
        T2["search_policy_wording<br/>BM25 locally, Azure AI Search hybrid<br/>(Azure untested)"]
        T3["check_duplicate"]
        T4["propose_claim<br/>proposes only, no writer"]
        AUD[("Audit log<br/>in memory, Table Storage<br/>(Azure untested)")]
        AG --- T1
        AG --- T2
        AG --- T3
        AG --- T4
        AG -.-> AUD
    end

    subgraph Review["Review (M1 API, M2 UI and intake)"]
        direction TB
        SB1[["Service Bus<br/>claims-for-review"]]
        API["ASP.NET Core API"]
        UI["Angular review UI<br/>(M2)"]
        SB1 --> API --> UI
    end

    subgraph Legacy["Legacy claims system (mock)"]
        direction TB
        LAPI["Minimal API<br/>GET /policies, POST /claims"]
        SQL[("Azure SQL<br/>(M3)")]
        LAPI --- SQL
    end

    Fn -- "claim-documents queue" --> AG
    DI --> AG
    T1 -- read --> LAPI
    T3 -- read --> LAPI
    T3 -- read --> API
    AG -- "extracted claim" --> SB1
    UI -- "handler approves" --> API
    API -- "idempotent write after approval" --> LAPI

    MI(["Managed identity<br/>and Key Vault"])
    OBS(["Application Insights<br/>OpenTelemetry"])
    MI -.-> Fn
    MI -.-> API
    MI -.-> AG
    Fn -.-> OBS
    API -.-> OBS
    AG -.-> OBS
```

Read the diagram left to right. A document lands in the inbox, an event starts the Function, the Function
sends the document for OCR and then to extraction. The assessor reads the policy, searches the policy wording,
checks for duplicates and proposes a route. The claim, its assessment and the audit trail of the tool calls go to a
queue. The API stores them and shows them to a handler. Only the handler's approval causes the API to write to the
legacy system.

## Components

| Component | Project | Status |
|---|---|---|
| Blob inbox, Event Grid | `infra/` | Storage account and container defined in Bicep. Event Grid subscription added after the first deployment. |
| Intake pipeline | `src/ClaimsIntake.Pipeline` | Built (M2). Host-free `ClaimIntakePipeline`: extract text, extract fields, `lookup_policy`, validate, hand off, one span per stage. |
| Intake Function | `src/ClaimsIntake.Functions` | Built (M2), never run. Blob trigger that calls the pipeline. |
| Local inbox watcher | `src/ClaimsIntake.Watcher`, `LocalInboxWatcher` | Built (M2). Local-mode stand-in for the blob trigger. |
| Document text | `LocalPdfTextExtractor`, `AzureDocumentIntelligenceExtractor` | Local reader built and tested. Document Intelligence version compiled and tested against a fake, never run. |
| Field extraction | `HeuristicClaimExtractor`, `FoundryLlmClaimExtractor` | Heuristic built and tested. Model version (`IChatClient`, JSON schema) tested against a fake chat client, never run. |
| Policy wordings | `samples/policy-wordings/` | Built (M3). Four synthetic wordings in markdown, chunked by clause. Each legacy seed policy names its wording. |
| Policy wording search | `Bm25PolicyWordingSearch`, `AzureAiSearchPolicyWordingSearch`, `ClaimsIntake.Ingestion` | Local BM25 built and tested. Azure hybrid search, the index schema and the ingestion upload compiled and tested against fakes, never run. |
| Agent tools | `ClaimTools` | Built (M3). `lookup_policy`, `search_policy_wording`, `check_duplicate`, `propose_claim` (proposes only), each audited and traced. |
| Claim assessment | `RuleBasedClaimAssessor`, `AgentClaimAssessor`, `CitationGuard`, `AssessmentRunner` | Rule-based built and run end to end. The Agent Framework assessor and the citation guard are tested with a scripted chat client, never run against a real model. |
| Audit log | `IAuditLog`, `InMemoryAuditLog`, `TableStorageAuditLog` | In memory built and run. Table Storage tested against a fake table, never run. `GET /claims/{id}/audit`. |
| Hand-off | `HttpClaimSink`, `ServiceBusClaimSink` | HTTP (local) built and run. Service Bus (Azure) compiled and tested against a fake sender, never run. |
| Domain and validation rules | `src/ClaimsIntake.Domain` | Built. Claim, fields with confidence and span, status, data-quality rules, `ProposedClaim` hand-off message. |
| Review API | `src/ClaimsIntake.Api` | Built, with an in-memory store: list, get, approve (writes to legacy), reject, audit trail, claim history for the duplicate check, intake route and Service Bus consumer, health checks, OpenTelemetry. |
| Review UI | `ui/` | Built (M2, M3). Angular 21: claim list, claim detail with source text beside fields, coverage findings with expandable cited clauses, route and audit trail, approve or reject. |
| Mock legacy system | `src/ClaimsIntake.Legacy` | Built, in memory: `GET /policies/{number}` (with the wording id), `GET /claims?policyNumber=`, `POST /claims` with an idempotency key. |
| Infrastructure | `infra/`, `azure.yaml` | Written, not deployed or validated. M3 adds an Azure AI Search resource (free tier by default, `basic` is about 70 pounds a month), the Search Index Data Reader role, an audit table and app settings. Document Intelligence and Foundry resources are not in it yet. |

## Two modes, one pipeline

The same `ClaimIntakePipeline` runs in both modes. Only the edges differ, and they are chosen by `Intake:*`
configuration.

| | Local mode | Azure mode |
|---|---|---|
| Trigger | `LocalInboxWatcher` polls `./local-inbox` | Blob created, Event Grid, blob-trigger Function |
| Text | PdfPig (text PDFs), `.txt`, `.eml` | Document Intelligence `prebuilt-layout` |
| Fields | `HeuristicClaimExtractor` | `FoundryLlmClaimExtractor` (`IChatClient`) |
| Policy search | BM25 over the clauses in `policy-wordings/` | Azure AI Search, hybrid keyword and vector |
| Assessor | `RuleBasedClaimAssessor` | `AgentClaimAssessor` (Microsoft Agent Framework) |
| Audit log | In memory in the API | Azure Table Storage |
| Hand-off | `POST /intake/claims` on the API | `claims-for-review` Service Bus queue |
| Credentials | None | `DefaultAzureCredential`, a managed identity, no keys |
| Review store | In-memory, in the API | In-memory, in the API (durable store is future work) |

Both hand-offs carry the same `ProposedClaim` JSON and end in `ClaimIntakeService`, which derives the claim id
from the document id so a repeat is recognised.

## Approval and the write to the legacy system

1. A handler approves a claim in the UI. The API checks that the legacy system can take it (policy number,
   claimant, readable loss date and description are present) and that it has no blocking errors.
2. The claim moves to `Approved`, and the API posts it to the legacy system with `Idempotency-Key` set to the claim id.
3. On success the claim moves to `Submitted` and keeps the legacy reference.
4. If the legacy system fails or times out, the claim stays `Approved` and the API answers 502. Approving again
   sends the same key, so the legacy system either creates the claim or returns the one it already made. Approvals
   for one claim run one at a time, and a claim already `Submitted` is returned as it is.

## Trust boundaries and data flow

1. **Outside to inbox.** Documents come from brokers and are untrusted. They are treated as data, never as
   instructions. They are size-limited (10 MB) and type-checked at intake.
2. **Inbox to extractor.** The extractor sees document text. In M2 the model has no tools, its reply is
   validated against the document, and a value it cannot ground in the text has its confidence capped. In M3 the
   assessing agent has read-only tools and `propose_claim` has no writer to call
   ([ADR 0002](adr/0002-human-approval-for-writes.md)). Tool arguments are treated as untrusted, the claim text is
   passed as data, and the citation guard removes any clause the search did not return
   ([ADR 0006](adr/0006-policy-wording-rag.md)).
3. **Pipeline to review store.** Proposed claims carry fields, confidence, source spans, validation issues and
   the source text. The local HTTP route has no authentication and is off by default.
4. **Review to legacy.** The only write path. It needs an approval from a named handler and uses an
   idempotency key.
5. **Telemetry and audit.** Traces, metrics and audit entries carry identifiers, counts and durations, never claim
   content or personal data. A search query is recorded as its length.

## Cross-cutting

- **Identity:** a managed identity and RBAC for every service-to-service call, no keys
  ([ADR 0003](adr/0003-managed-identity-no-keys.md)).
- **Region:** UK South for all resources. The Bicep restricts the `location` parameter to `uksouth`.
- **Observability:** OpenTelemetry in the API and the watcher (console exporter by default, OTLP optional). The
  pipeline emits an `intake.<stage>` span per stage with `stage`, `duration_ms`, `documentId` and `confidence`, and a
  `tool.<name>` span per tool call, and never document text or field values. Application Insights is created in the infrastructure. Exporting to it,
  including the Function App's spans, is wired in Milestone 6.
- **Configuration:** settings name resources, not secrets. Key Vault is provisioned and empty.

## Deployment (not yet exercised)

`azd up` provisions a resource group with: a user-assigned managed identity, Log Analytics and Application
Insights, two storage accounts (inbox, and Function host), Service Bus with two queues, Key Vault, a Flex
Consumption Function App, an App Service plan with the API and the legacy mock, and (Milestone 3) an Azure AI Search
service and an audit table on the host storage account. The Bicep has not been validated or deployed. See the README.
