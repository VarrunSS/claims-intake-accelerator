# Claims Intake Accelerator

A reference implementation of a document-to-claim pipeline for a UK commercial insurer, built on Azure with
.NET 10. It reads messy claim emails and PDFs, extracts the claim, checks it against the policy, and puts a
pre-filled claim in front of a handler. A person approves every write to the claims system.

> **Thames Mutual Insurance is a fictional customer.** The scenario, people, policies and numbers are invented
> for this portfolio project. No real claim data is used or needed.

It is run like a customer engagement: discovery notes, agreed success metrics, written design decisions, tests,
CI and infrastructure as code, with the work delivered in milestones.

## Status

Milestones 1 to 4 are done. The rest is planned and not started. Everything runs locally with only the .NET
SDK (and Node for the UI); **the Azure paths of Milestones 2 and 3 have not been run** (see below).

| # | Milestone | Status |
|---|---|---|
| 1 | Discovery and skeleton: docs, domain rules, review API, legacy mock, Function stub, Bicep, CI | **Done** |
| 2 | Happy path: document in, extracted and validated claim in the Angular review UI, approval writes to the legacy system | **Done locally. Azure path compiled and unit-tested only, never run** |
| 3 | RAG and legacy tools: policy wording search with citations, agent tools, coverage assessment, audit log | **Done locally. Azure path (AI Search, agent on a real model, Table Storage) compiled and unit-tested against fakes only, never run** |
| 4 | Evals and data quality: synthetic claims, scorer, eval gate in CI, input-quality checks | **Done locally.** Local pipeline only, on 36 held-out synthetic claims: field F1 0.968, route accuracy 88.9%, citations 100% grounded (by construction), 41.7% auto-routable. **No real-model number exists yet** |
| 5 | Model comparison: 2 to 3 Foundry models on accuracy, cost and latency, written up as an ADR | Not started |
| 6 | Security and observability: split identities, PII redaction, threat model, App Insights workbook | Not started |
| 7 | Packaging: demo, runbook, final README and measured results | Not started |

**The infrastructure has not been deployed or validated.** `azd`, `az` and the Functions Core Tools were not
installed when Milestones 1 and 2 were written, so the Bicep in `infra/` has never been through `az bicep build`,
`what-if` or a deployment, and the Function App has never been started. Expect fixes on the first real
deployment. The .NET code is built and tested; see below.

**Milestones 2 and 3 were built without any Azure or AI access** (no subscription, no model keys, no Docker). Every
external dependency is behind an interface with a local implementation, which is what runs by default and in
the tests, and an Azure implementation, chosen by configuration ([ADR 0005](docs/adr/0005-local-development-mode.md)).
The Azure implementations (Document Intelligence, the Foundry model, Service Bus, the Function, and in Milestone 3
Azure AI Search, the Agent Framework assessor and the Table Storage audit log) compile and are unit-tested against
fakes. They have never talked to the real services.

## Architecture

The full Mermaid diagram and a component table are in [docs/architecture.md](docs/architecture.md). In short:

```
Blob inbox -> Event Grid -> Azure Functions -> Document Intelligence -> extraction agent
   -> Service Bus -> ASP.NET Core API -> Angular review UI -> mock legacy claims system
```

Locally, a folder watcher stands in for the blob trigger, a PDF reader and a heuristic extractor stand in for
Document Intelligence and the model, and the hand-off is an HTTP call instead of a queue message. The pipeline
code between them is the same.

Design decisions are in [docs/adr](docs/adr):

1. [Event-driven pipeline](docs/adr/0001-event-driven-pipeline.md)
2. [Human approval for writes](docs/adr/0002-human-approval-for-writes.md)
3. [Managed identity, no keys](docs/adr/0003-managed-identity-no-keys.md)
4. [Microsoft Agent Framework for the agent](docs/adr/0004-dotnet-agent-framework.md)
5. [Local development mode](docs/adr/0005-local-development-mode.md)
6. [Policy wording search, agent tools and the citation guard](docs/adr/0006-policy-wording-rag.md)
7. [Evaluation, input quality and a deterministic notification date](docs/adr/0007-evaluation-approach.md)

Customer documents: [discovery](docs/00-discovery.md) and [success metrics](docs/01-success-metrics.md).

## What is built

### Milestone 1

| Project | What it does |
|---|---|
| `src/ClaimsIntake.Domain` | `Claim`, `ExtractedField` (value, confidence, source span), `PolicyReference`, `ClaimStatus`, and `ClaimValidator`, which flags a missing policy number, an unknown policy, a missing, unreadable or future loss date, a loss date outside the policy period, an invalid amount and low-confidence fields. `Claim.Approve` refuses a claim with blocking errors. |
| `src/ClaimsIntake.Api` | Minimal API: list claims for review, get one, approve, reject. In-memory store behind `IClaimRepository`. Liveness and readiness health checks. OpenTelemetry traces and metrics with a configurable exporter. |
| `src/ClaimsIntake.Legacy` | Mock legacy claims system: `GET /policies/{number}` over 10 seeded policies, and `POST /claims`, which requires an `Idempotency-Key` header and makes retries safe. |
| `src/ClaimsIntake.Functions` | .NET 10 isolated worker with the blob trigger. |
| `infra/`, `azure.yaml` | Bicep for `azd up`: managed identity with role assignments, storage, Service Bus, Key Vault, Log Analytics and Application Insights, a Function App and an App Service plan for the API and the mock. |

### Milestone 2: the happy path

A claim document dropped in the inbox comes out as an extracted, validated claim in the review UI, where a handler
approves or rejects it. Approval writes to the legacy system exactly once.

| Project | What it adds |
|---|---|
| `src/ClaimsIntake.Pipeline` | `ClaimIntakePipeline`: extract text, extract fields, look up the policy (`lookup_policy`, over a typed `HttpClient`), validate with `ClaimValidator`, hand off. One OpenTelemetry span per stage with `stage`, `duration_ms`, `documentId` and `confidence`; no document text in spans or logs. Two implementations of each seam, chosen by `Intake:*` settings: `IDocumentTextExtractor` (`LocalPdfTextExtractor` on PdfPig, or `AzureDocumentIntelligenceExtractor`), `IClaimExtractor` (`HeuristicClaimExtractor`, deterministic and offline, or `FoundryLlmClaimExtractor` on `IChatClient` with a JSON schema) and `IClaimSink` (`HttpClaimSink` or `ServiceBusClaimSink`). Also `LocalInboxWatcher`. |
| `src/ClaimsIntake.Watcher` | A small host that runs the folder watcher. |
| `src/ClaimsIntake.Functions` | The blob trigger now calls the pipeline, and the hand-off goes to the `claims-for-review` Service Bus queue. |
| `src/ClaimsIntake.Api` | `POST /intake/claims` (local mode only, off by default) and a Service Bus consumer, both ending in `ClaimIntakeService`. Approval now writes to the legacy system with the claim id as the idempotency key and records the legacy reference. CORS for the Angular dev server in Development only. |
| `src/ClaimsIntake.Domain` | `ProposedClaim` (the hand-off message), source text and legacy reference on `Claim`. |
| `ui/` | Angular review UI: claim list with status, confidence and issue counts; claim detail with the source text beside the extracted fields, confidence badges, validation issues, and approve or reject with a reason. |
| `samples/` | Eight synthetic claim documents, including a missing policy number, a future loss date, a loss outside the policy period, an informal email, a broker `.eml` and a garbled layout, and the script that generates them. |
| `tests/` | Domain, API and Pipeline tests; see the Quickstart for counts. |

**How the heuristic extractor decides its confidence.** A value next to its label (`Policy number: TM-CP-100001`)
scores 0.90 to 0.97. A value found by pattern or keyword alone scores 0.40 to 0.88, and a guess (the email sender
as the insured, the first pound amount in a message) scores 0.45 to 0.55. Several different policy numbers, or a
date that could be read day-first or month-first, lower it further. Anything below 0.80 is flagged to the handler.
The extractor says "not found" rather than invent a value. It does not read scans.

**Why `IChatClient` and not the agent yet.** [ADR 0004](docs/adr/0004-dotnet-agent-framework.md) chose Microsoft
Agent Framework. The single extraction call in this milestone needs no tools or threads, so
`FoundryLlmClaimExtractor` uses `Microsoft.Extensions.AI` `IChatClient`, the layer Agent Framework sits on, behind
`IClaimExtractor`. The agent and its tools arrive in Milestone 3. The model's answer is checked: a value that is
not in the document has its confidence capped at 0.5, and a malformed reply fails the document instead of
producing an empty claim.

### Milestone 3: RAG and legacy tools

The pipeline now checks each claim against the policy wording it is written on and hands the handler an assessment:
the clauses that matter, quoted, a recommended route, and an audit trail of every tool call. It is advice. A handler
still approves or rejects every claim, and nothing in this milestone writes to the legacy system
([ADR 0002](docs/adr/0002-human-approval-for-writes.md), [ADR 0006](docs/adr/0006-policy-wording-rag.md)).

| Piece | What it does |
|---|---|
| `samples/policy-wordings/` | Four synthetic Thames Mutual wordings (Commercial Combined, with flood extension, Office, Engineering) in markdown, with numbered sections and clauses: insuring clause, definitions, insured perils, exclusions (flood, wear and tear, unoccupied over 30 days), conditions (notification period) and excess. Each seeded legacy policy points at one through `wordingId`. |
| `IPolicyWordingSearch` | Returns ranked clauses (wording id, clause reference, section, heading, text, score). Local: clause-level chunking and BM25, written in full, deterministic. Azure: `AzureAiSearchPolicyWordingSearch`, a hybrid keyword and vector query with `DefaultAzureCredential`, embeddings through `IEmbeddingGenerator`. The index schema is in code (`PolicyWordingIndex`) and `src/ClaimsIntake.Ingestion` chunks, embeds and uploads the wordings (`--dry-run` needs no Azure). |
| `ClaimTools` | The four agent tools as plain C# methods: `lookup_policy`, `search_policy_wording`, `check_duplicate` (same policy and loss date, amount within 10 per cent, in the review store or the legacy system) and `propose_claim`, which only records a proposal and has no way to write. |
| `IClaimAssessor` | `RuleBasedClaimAssessor` (local, deterministic) calls the same tools in a fixed order. `AgentClaimAssessor` uses Microsoft Agent Framework 1.28.0 over `IChatClient` with the tools registered as `AIFunction`s. Chosen by `Intake:Assessor` (`Rules`, `Agent`, `None`) and run in `ClaimIntakePipeline` after validation. |
| Citation guard | A finding may only cite a clause that `search_policy_wording` returned in that run. Anything else is removed, flagged and sends the claim to senior review; the clause text shown is the wording's, never the model's. |
| Audit log | Every tool call: claim id, tool, redacted arguments (ids, dates and the length of free text), result summary, duration, time. `GET /claims/{id}/audit`. In memory locally, Azure Table Storage when `Audit:Table:Endpoint` is set. |
| Telemetry | A span per tool call (`tool.<name>`) and one for the assessment. Ids, durations and counts only. |
| `ui/` | The claim detail shows each finding with its cited clause (expandable), the recommended route and the audit trail. |
| `samples/` | Three more documents: a flood claim against a wording that excludes flood, a claim for a building unoccupied for 45 days that was also notified late, and a chasing email that duplicates sample 01. |

**What the local assessor catches, and does not.** It matches keywords in the loss description to a handful of
causes (fire, flood, theft, storm, impact, refrigeration failure, breakdown, escape of water) and three exclusions,
and it reads the notification period from the wording. "The river flooded the yard" is flood; "water came in
through the roof" is escape of water. It will miss what its patterns do not name, and says so in the assessment's
notes. It is the baseline the agent is compared with in Milestone 4, not a substitute for it.

### Milestone 4: evaluation and data quality

An evaluation suite measures the pipeline, input-quality checks send unusable documents to a person, and CI fails below a
threshold. All of it runs offline. **It measures the local heuristic extractor and rules assessor only; real-model numbers are
not measured yet** (Milestone 5 adds the model comparison). The data is synthetic and written by the same author as the
extractor, which flatters it ([evals/README.md](evals/README.md), [ADR 0007](docs/adr/0007-evaluation-approach.md)).

| Piece | What it does |
|---|---|
| `evals/dataset/` | 51 synthetic messy claims with ground truth, tagged by difficulty (clean, email, `.eml`, missing fields, amounts in words, UK dates, policy typos, garbled layout, exclusions, late notification, duplicates, non-claims, unreadable files). Generated deterministically by `evals/generate_dataset.py`. Split into 15 `dev` (look at it, tune on it) and 36 `test` (held out). |
| `evals/ClaimsIntake.Evals` | A .NET console that runs the real pipeline in process and writes `report.md` and `results.json`: field precision, recall and F1, issue and finding precision and recall, route accuracy and confusion matrix, citation groundedness, auto-routable rate, silent error rate, latency per stage, per-tag breakdown. Exits 1 below `evals/thresholds.json`. |
| Input-quality checks | `IInputQualityCheck`: empty or near-empty text, not English or not a claim, oversized, password-protected or corrupt PDF, duplicate file hash. A failing document reaches the review queue as a claim with one blocking issue that says why. |
| Date fix | The notification period is measured to the date the document states, otherwise to when it was received, never to the clock at processing time. The samples state a notification date, so the demo no longer drifts. |
| CI | The `evals` job runs the test split, writes the report to the job summary, uploads it, and fails the build below threshold. |

Measured on the test split (committed baseline in `evals/results/baseline/`): field F1 **0.968** (policy number 0.982, loss
date 0.982, claimant 0.967, amount 0.982, cause 0.889, notification date 1.000); issue precision 0.833 and recall 0.909;
finding precision 1.000 and recall 0.853; route accuracy **88.9%**; **41.7%** auto-routable and 58.3% needing a person first.
The weak spots are emails (route accuracy 63.6% over 11 cases), unusual phrasing of the cause of loss, and a silent-error
rate of 7.1% (1 of 14 unflagged claims) against a 1% guardrail, which is not met.

## Quickstart

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), and [Node.js](https://nodejs.org) 22 or later
for the UI. No Azure account, no keys, no Docker.

```bash
dotnet build
dotnet test            # Domain 79, API 113, Pipeline 332, Evals 81 (605 in all)
dotnet run --project evals/ClaimsIntake.Evals -- --split test --thresholds evals/thresholds.json   # the evaluation, offline
cd ui && npm ci && npm test && npm run build    # UI: 35 tests, run headless in jsdom
```

### Run the whole flow locally

Four processes, in four terminals (or run `./scripts/run-local.ps1` on Windows to start them all):

```bash
dotnet run --project src/ClaimsIntake.Legacy      # http://localhost:5200  mock claims system
dotnet run --project src/ClaimsIntake.Api         # http://localhost:5100  review API (Development: intake route on)
dotnet run --project src/ClaimsIntake.Watcher     # watches ./local-inbox
cd ui && npm start                                 # http://localhost:4200  review UI, proxies /api to the API
```

Then drop documents into the inbox and open http://localhost:4200:

```bash
cp samples/01-water-damage-clean.pdf samples/05-loss-outside-policy-period.pdf samples/06-email-fire.txt local-inbox/
curl "http://localhost:5100/claims?status=all"
```

To see the coverage assessment, drop the clean sample and one that triggers exclusions, then read the claim and its
audit trail (each sample states its own notification date, so the result does not depend on the day you run it):

```bash
cp samples/01-water-damage-clean.pdf samples/09-flood-office-exclusion.pdf samples/10-unoccupied-late-notification.pdf local-inbox/
cp samples/11-duplicate-water-damage-email.txt local-inbox/      # after 01 has been processed: a possible duplicate of it
curl http://localhost:5100/claims/<id>          # "assessment": findings with cited clauses, route, notes
curl http://localhost:5100/claims/<id>/audit    # every tool call, with redacted arguments
```

Sample 01 is covered under clause 3.2 and goes straight to a handler. Sample 09 cites the flood exclusion (4.1) of the
Office wording. Sample 10 cites the unoccupied exclusion (4.3) and the 30-day notification condition (5.1) and is
marked for senior review.

Each file moves to `local-inbox/processed/`, or to `failed/` if it cannot be read. In the UI, a handler opens a
claim, checks the fields against the source text, and approves or rejects it. Approving a claim with a blocking
error is refused. Approving a clean claim writes it to the legacy system and shows the legacy reference:

```bash
curl -X POST http://localhost:5100/claims/<id>/approve -H "Content-Type: application/json" -d '{"reviewer":"a.patel"}'
```

Approve it again and you get the same reference back with an `Idempotent-Replay: true` header, and the legacy system
still holds one claim. If the legacy system is down, the claim stays approved, the API answers 502, and approving
again completes it under the same idempotency key. See `samples/` for what each document is meant to show.

To use real Azure services instead, see the `Intake:*` settings in
[ServiceCollectionExtensions](src/ClaimsIntake.Pipeline/ServiceCollectionExtensions.cs). They are untested against
the real services. For Milestone 3: `Intake:PolicySearch` (`Local` or `AzureAiSearch`, with
`Intake:AzureAiSearch:Endpoint` and `Intake:Foundry:EmbeddingDeployment`), `Intake:Assessor` (`Rules`, `Agent` or
`None`; `Agent` needs `Intake:Foundry:Endpoint` and `Deployment`), and on the API `Audit:Table:Endpoint`. To load the
wordings into the index (needs `az login` and the Search Service Contributor and Search Index Data Contributor
roles on the service):

```bash
dotnet run --project src/ClaimsIntake.Ingestion -- --wordings samples/policy-wordings --dry-run   # chunk and print, offline
dotnet run --project src/ClaimsIntake.Ingestion -- --wordings samples/policy-wordings \
  --search-endpoint https://<service>.search.windows.net --openai-endpoint https://<resource>.openai.azure.com \
  --embedding-deployment <name>
```

### The pieces separately

Run the review API (http://localhost:5100). To load four made-up claims for a demo without the watcher, set
`Seed__DemoData=true`.

```bash
dotnet run --project src/ClaimsIntake.Api

curl http://localhost:5100/claims                    # claims waiting for review, oldest first
curl http://localhost:5100/claims/<id>               # one claim: fields, confidence, source, issues
curl -X POST http://localhost:5100/claims/<id>/approve \
     -H "Content-Type: application/json" -d '{"reviewer":"a.patel"}'
curl -X POST http://localhost:5100/claims/<id>/reject \
     -H "Content-Type: application/json" -d '{"reviewer":"a.patel","reason":"Duplicate claim"}'
curl http://localhost:5100/health/ready
```

Run the mock legacy system (http://localhost:5200):

```bash
dotnet run --project src/ClaimsIntake.Legacy

curl http://localhost:5200/policies/TM-CP-100001
curl -i -X POST http://localhost:5200/claims \
     -H "Content-Type: application/json" -H "Idempotency-Key: demo-1" \
     -d '{"policyNumber":"TM-CP-100001","claimantName":"Brindle & Marsh Joinery Ltd","lossDate":"2026-11-03","description":"Burst pipe","estimatedAmountGbp":12500}'
```

Send the same request again with the same key and you get the original claim back with an
`Idempotent-Replay: true` header. Use the same key with a different body and you get a 409.

Telemetry: set `Telemetry__Exporter` to `console` (default), `otlp` (honours the standard
`OTEL_EXPORTER_OTLP_ENDPOINT` variable) or `none`.

Coverage, as CI runs it: `dotnet test --collect:"XPlat Code Coverage"`.

## Security posture

What is in place in Milestone 1, and what is not.

In place:

- **No keys or connection strings.** The Bicep gives one user-assigned managed identity scoped role
  assignments, turns off shared-key access on storage and local auth on Service Bus, and puts only resource
  names in application settings ([ADR 0003](docs/adr/0003-managed-identity-no-keys.md)).
- **Human approval for writes.** The agent can only propose. `propose_claim` records a proposal in memory; the
  tools are given no writer, so they cannot write (a test checks this). The domain model refuses to approve a claim that
  has data-quality errors or has already been decided ([ADR 0002](docs/adr/0002-human-approval-for-writes.md)).
- **UK South only.** The Bicep `location` parameter allows only `uksouth`.
- **Safe retries.** The legacy write needs an idempotency key. Reuse with a different body is refused. The API
  uses the claim id as the key, serialises approvals per claim, and a repeated approval returns the first result.
- **No document text in telemetry.** Spans and logs carry document and claim ids, stage names, durations,
  counts and confidence, and exception types, never text or field values. Tests check this.
- **Untrusted documents.** Document text goes to the model as data between delimiters with no tools, the reply is
  validated against the document, and size limits apply at intake (10 MB per document) and on the hand-off.
- **Input handling.** Request bodies are validated and length-limited. Blob names are cleaned before logging.
- **Defaults on the hosts.** HTTPS only, TLS 1.2 minimum, FTP off, basic publishing credentials off,
  Key Vault with RBAC, soft delete and purge protection, storage with no public blob access.

- **Grounded citations.** A coverage finding can only cite a clause that the wording search returned in that run, and
  the clause text shown is the wording's ([ADR 0006](docs/adr/0006-policy-wording-rag.md)).
- **Audit of every tool call**, with arguments reduced to ids, dates and lengths of free text, not claim text.

Not yet in place (planned, mainly Milestone 6):

- `POST /intake/claims` is off unless `Intake:HttpEndpoint:Enabled` is set, and has **no authentication**; it is for
  local mode only ([ADR 0005](docs/adr/0005-local-development-mode.md)).
- The review API has **no authentication**. The reviewer name comes from the request body. Entra ID sign-in and
  roles come next. Do not expose the API as it stands.
- The mock legacy system has no authentication either. It stands in for a system that would have its own.
- One identity is shared by all three apps. It will be split by component.
- No private endpoints or network restrictions. No Azure Policy. No PII redaction in telemetry (nothing logs
  claim content today, but nothing enforces it yet). No threat model.
- Application Insights still accepts connection-string ingestion.
- Nothing has been deployed, so none of the Azure configuration has been tested.

## What was not verified

- The Bicep has not been compiled with `az bicep build`, checked with `what-if`, or deployed. In particular,
  these are unconfirmed: .NET 10 on Flex Consumption (`dotnet-isolated`, `10.0`), `DOTNETCORE|10.0` on
  Linux App Service in UK South, the `azd` `host: function` setting for a Flex plan, the Function host
  settings for identity-based blob trigger, Service Bus and host storage connections, and azd deployment with
  basic publishing credentials turned off. Role definition ids were checked against Microsoft's published
  built-in roles list.
- **Milestone 3 Azure paths, compiled and tested only against fakes, never run:** `AzureAiSearchPolicyWordingSearch`
  (the keyword and vector query, options, filter and result mapping are tested; `SearchClientClauseQuery` and
  `SearchIndexClauseWriter`, which call the SDK, are not), the index schema (checked as an object, never created),
  the ingestion tool's upload path, `AgentClaimAssessor` against a real model (tested with a scripted `IChatClient`,
  so how a real model behaves with the prompt, the tools and the JSON answer is unknown), `TableStorageAuditLog`
  (against a fake table; `TableClientAuditTable` is not tested), and the new Bicep (an Azure AI Search resource, the
  Search Index Data Reader role, the audit table and app settings, none of it through `az bicep build`). The Foundry
  chat and embedding deployments are still not in the Bicep, so the Function App is configured for the local search and
  the rule-based assessor. Whether the free Azure AI Search tier accepts Entra ID only access is assumed, not checked.
- The rule-based assessor was tuned on the eleven samples, so its results on them flatter it. Retrieval quality on
  real, longer wordings is unmeasured. The UI changes for the assessment were tested in jsdom and not looked at in a
  browser.
- The Function App has not been started, because the Functions Core Tools are not installed. The project
  builds, the function is unit-tested by calling it directly with fakes, and the Event Grid blob trigger is only
  compiled, not run. The pipeline's spans are not yet wired to Application Insights in the Function App.
- **Milestone 2 Azure paths, compiled and tested only against fakes, never run:**
  `AzureDocumentIntelligenceExtractor` (its `DocumentIntelligenceClient` adapter is not even unit-tested),
  `FoundryLlmClaimExtractor` against a real model (the JSON schema is hand-written and not checked against a live
  service's strict-mode rules), `ServiceBusClaimSink`, the API's `ServiceBusClaimConsumer`, and the new Bicep app
  settings. The Document Intelligence and Foundry resources are not in the Bicep yet. Extraction quality on real
  documents or with a real model is unmeasured; the 50-claim evaluation set is Milestone 4.
- The heuristic extractor was tuned on the eight synthetic samples in this repository, so its results on them
  flatter it.
- The UI is tested in jsdom, and was looked at once in a real browser against the running API. It has no
  automated browser or accessibility test.
- The Mermaid diagram was parsed by the Mermaid library but not viewed in a renderer.

## Repository layout

```
.github/workflows/ci.yml   build, test with coverage; eval job commented out until Milestone 4
docs/                      discovery, success metrics, architecture, ADRs
infra/                     Bicep (main.bicep and modules), azd parameters
src/                       Domain, Pipeline, Api, Legacy, Functions, Watcher, Ingestion
ui/                        Angular review UI
samples/                   synthetic claim documents and their generator; policy-wordings/ holds the wordings
scripts/                   run-local.ps1 starts everything locally
tests/                     Domain.Tests, Pipeline.Tests, Api.Tests (API and legacy)
azure.yaml                 azd project definition
```

## Licence

MIT. See [LICENSE](LICENSE).
