# What is built, milestone by milestone

> Thames Mutual Insurance is a fictional customer. This is the detail behind the [README](../README.md); [docs/architecture.md](architecture.md) has the diagram.

## Milestone 1

| Project | What it does |
|---|---|
| `src/ClaimsIntake.Domain` | `Claim`, `ExtractedField` (value, confidence, source span), `PolicyReference`, `ClaimStatus`, and `ClaimValidator`, which flags a missing policy number, an unknown policy, a missing, unreadable or future loss date, a loss date outside the policy period, an invalid amount and low-confidence fields. `Claim.Approve` refuses a claim with blocking errors. |
| `src/ClaimsIntake.Api` | Minimal API: list claims for review, get one, approve, reject. In-memory store behind `IClaimRepository`. Liveness and readiness health checks. OpenTelemetry traces and metrics with a configurable exporter. |
| `src/ClaimsIntake.Legacy` | Mock legacy claims system: `GET /policies/{number}` over 10 seeded policies, and `POST /claims`, which requires an `Idempotency-Key` header and makes retries safe. |
| `src/ClaimsIntake.Functions` | .NET 10 isolated worker with the blob trigger. |
| `infra/`, `azure.yaml` | Bicep for `azd up`: managed identity with role assignments, storage, Service Bus, Key Vault, Log Analytics and Application Insights, a Function App and an App Service plan for the API and the mock. |

## Milestone 2: the happy path

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

**Why `IChatClient` and not the agent yet.** [ADR 0004](adr/0004-dotnet-agent-framework.md) chose Microsoft
Agent Framework. The single extraction call in this milestone needs no tools or threads, so
`FoundryLlmClaimExtractor` uses `Microsoft.Extensions.AI` `IChatClient`, the layer Agent Framework sits on, behind
`IClaimExtractor`. The agent and its tools arrive in Milestone 3. The model's answer is checked: a value that is
not in the document has its confidence capped at 0.5, and a malformed reply fails the document instead of
producing an empty claim.

## Milestone 3: RAG and legacy tools

The pipeline now checks each claim against the policy wording it is written on and hands the handler an assessment:
the clauses that matter, quoted, a recommended route, and an audit trail of every tool call. It is advice. A handler
still approves or rejects every claim, and nothing in this milestone writes to the legacy system
([ADR 0002](adr/0002-human-approval-for-writes.md), [ADR 0006](adr/0006-policy-wording-rag.md)).

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

## Milestone 4: evaluation and data quality

An evaluation suite measures the pipeline, input-quality checks send unusable documents to a person, and CI fails below a
threshold. All of it runs offline. **It measures the local heuristic extractor and rules assessor only; real-model numbers are
not measured yet** (Milestone 5 adds the model comparison). The data is synthetic and written by the same author as the
extractor, which flatters it ([evals/README.md](../evals/README.md), [ADR 0007](adr/0007-evaluation-approach.md)).

| Piece | What it does |
|---|---|
| `evals/dataset/` | 58 synthetic messy claims with ground truth, tagged by difficulty (clean, email, `.eml`, missing fields, amounts in words, UK dates, policy typos, garbled layout, exclusions, late notification, duplicates, non-claims, unreadable files, and seven prompt-injection documents tagged `injection`). Generated deterministically by `evals/generate_dataset.py`. Split into 18 `dev` (look at it, tune on it) and 40 `test` (held out). The seven injection cases were added in Milestone 6a after the first 51, which are byte-identical. |
| `evals/ClaimsIntake.Evals` | A .NET console that runs the real pipeline in process and writes `report.md` and `results.json`: field precision, recall and F1, issue and finding precision and recall, route accuracy and confusion matrix, citation groundedness, auto-routable rate, silent error rate, latency per stage, per-tag breakdown. Exits 1 below `evals/thresholds.json`. |
| Input-quality checks | `IInputQualityCheck`: empty or near-empty text, not English or not a claim, oversized, password-protected or corrupt PDF, duplicate file hash. A failing document reaches the review queue as a claim with one blocking issue that says why. |
| Date fix | The notification period is measured to the date the document states, otherwise to when it was received, never to the clock at processing time. The samples state a notification date, so the demo no longer drifts. |
| CI | The `evals` job runs the test split, writes the report to the job summary, uploads it, and fails the build below threshold. |

Measured on the 40-case test split (committed baseline in `evals/results/baseline/`): field F1 **0.969** (policy number 0.985, loss
date 0.985, claimant 0.971, amount 0.984, cause 0.885, notification date 1.000); issue precision 0.833 and recall 0.909;
finding precision 1.000 and recall 0.850; route accuracy **87.5%** (35 of 40); **37.5%** auto-routable and 62.5% needing a person first.
The weak spots are emails (route accuracy 58.3% over 12 cases), unusual phrasing of the cause of loss, and a silent-error
rate of 5.9% (1 of 17 unflagged claims) against a 1% guardrail, which is not met. These figures were first measured at Milestone 4 on a
36-case split and re-measured after Milestone 6a added four injection cases to it.

## Milestone 5: model comparison and prompt tuning harness

**Built and tested against scripted fakes. No real model has been run**, because this project's environment has no Azure AI
Foundry deployment and no AI credentials. There are no real-model accuracy, cost or latency numbers anywhere in this
repository, and the committed comparison says "not yet run against real models". With a Foundry project it is one command
([runbook](runbook-model-comparison.md)). Method and decision rules: [ADR 0008](adr/0008-model-selection-and-tuning.md).

| Piece | What it does |
|---|---|
| `evals/models.json` | The registry: three **placeholder** candidates (a GPT-family model, a Claude model on Foundry, a small catalogue model), each with kind, endpoint, deployment, a GBP price per 1M tokens with source URL and date. Every price is marked `verify-before-use`. No keys: `DefaultAzureCredential`. |
| `--extractor llm --assessor agent --model <id>` | Runs the production extractor and agent assessor over a client built from the registry (`azure-openai`, `azure-ai-inference` or `foundry-anthropic`). Records tokens per call, £ per claim, latency, 429 retries (bounded parallelism, back-off), content-filter refusals, malformed and schema-violating replies, and citations the guard removed. |
| `prompts/` | Versioned prompts, embedded in the pipeline. `--prompt v1\|v2`; `results.json` records the version and a SHA-256 of the text. v1 is what shipped; v2 is a **hypothesis** aimed at the Milestone 4 weaknesses and is not claimed to be better ([prompts/README.md](../prompts/README.md)). |
| `compare` | Runs model x prompt configurations over one split and writes `comparison.md`: F1, route accuracy, groundedness, guard removals, malformed rate, p50 and p95, £ per claim and per 1,000, and a recommendation **generated from documented decision rules** (cheapest configuration that passes every gate, ties broken by p95). The heuristic baseline is one row and runs for real. |
| `--dev-fakes` | Dev-only. `fake-oracle` and `fake-noisy` are scripted stand-ins that know the answers. They exercise token accounting, cost maths, the malformed-output and 429 paths and the recommendation end to end. Any output from them is labelled FAKE, and none is committed. |
| `infra/modules/foundry.bicep` | An AIServices account, model deployments as a parameter, local auth off, and roles for the managed identity. Function app settings switch on `Intake:ClaimExtractor=Foundry` and `Intake:Assessor=Agent`. **Never built or deployed.** |
| [ADR 0008](adr/0008-model-selection-and-tuning.md) | How models are chosen, when fine-tuning would be worth it, and the data and UK GDPR implications. Fine-tuning is not implemented. |
