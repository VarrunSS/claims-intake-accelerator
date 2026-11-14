# ADR 0005: Local development mode

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](../00-discovery.md).

- Status: accepted
- Date: 2026-11-14

## Context

The system is designed for Azure ([ADR 0001](0001-event-driven-pipeline.md)): Blob Storage, Event Grid, an
Azure Function, Document Intelligence, a model on Foundry, Service Bus and managed identities
([ADR 0003](0003-managed-identity-no-keys.md)). A new developer, a reviewer reading the repository, and the CI
job have none of that: no subscription, no model keys, no Docker for an emulator, and not always the Functions
Core Tools. Milestone 2 still has to be something they can run, see and test end to end.

What has to stay true in both modes:

- The intake logic is the same code, so what is tested locally is what runs in Azure.
- Every write to the legacy system still needs a handler's approval
  ([ADR 0002](0002-human-approval-for-writes.md)).
- Nothing in the local mode weakens the Azure mode, and the Azure mode never uses keys.

## Decision

Put the intake logic in a host-free class, `ClaimIntakePipeline` in `ClaimsIntake.Pipeline`, and put each
external dependency behind an interface with two implementations chosen by configuration under `Intake`.

| Seam | Local (default) | Azure |
|---|---|---|
| `IDocumentTextExtractor` | `LocalPdfTextExtractor`: PdfPig for text-based PDFs, plain `.txt` and `.eml` | `AzureDocumentIntelligenceExtractor`: `prebuilt-layout`, `DefaultAzureCredential` |
| `IClaimExtractor` | `HeuristicClaimExtractor`: regular expressions and keywords, offline, deterministic | `FoundryLlmClaimExtractor`: `IChatClient` with a JSON schema, `DefaultAzureCredential` |
| Trigger | `LocalInboxWatcher`, a `BackgroundService` that scans `./local-inbox` | The blob-trigger Function (Event Grid source) |
| `IClaimSink` (hand-off) | `HttpClaimSink`: `POST /intake/claims` on the review API | `ServiceBusClaimSink`: the `claims-for-review` queue |
| `IPolicyLookup` | `LegacyPolicyLookup` over a typed `HttpClient` to the legacy mock | The same class, pointed at the deployed legacy app |

The watcher runs as its own small host (`ClaimsIntake.Watcher`), next to the legacy mock and the API. The Function
and the watcher both only build an `IntakeDocument` and call the pipeline. With no settings at all, everything is
local.

### How the review store is reached

The pipeline and the API are separate processes in both modes, as they would be in Azure, so the review store
cannot be shared in memory. The options were:

1. **Host the watcher inside the API process** and hand over in memory. Simplest, but then the local mode has a
   different shape from Azure, and the API would carry a folder watcher and the pipeline's dependencies
   (PdfPig, Azure SDKs) that it does not need in production.
2. **A shared file or database** for the review store. Another thing to set up, and it would force a durable
   store into Milestone 2 for the sake of a demo.
3. **Hand over over HTTP.** The watcher posts the proposed claim to `POST /intake/claims`. The API accepts it
   through the same `ClaimIntakeService` that its Service Bus consumer uses, so both routes behave identically.

We chose **3**. It is the smallest change that keeps the process boundary, the message (`ProposedClaim`, the same
JSON in both modes) and the intake rules the same as Azure. The only code that differs by mode is the transport.

### Safeguards on the HTTP route

- It is **off unless** `Intake:HttpEndpoint:Enabled` is `true`. `appsettings.Development.json` turns it on; the
  base `appsettings.json` and the Bicep do not, so a deployed API does not expose it.
- It is **mapped only in the Development environment** and needs an app-only token with the `Intake.Submit` role
  (Milestone 6a, [ADR 0009](0009-identity-roles-and-segregation-of-duties.md)): a stray setting in Azure leaves it absent. Locally the watcher sends the
  `dev-app-pipeline` dev token. In Azure the hand-off is a Service Bus message, authorised by RBAC.
- The body is read with a size limit (8 MB), parsed defensively and checked for shape and size
  (`ClaimIntakeService.Check`). A body the domain types refuse is a 400, not a 500.
- The claim id is derived from the document id, so a document handed over twice, by a retry or a redelivered
  message, is one claim. The API answers 200 instead of 201 for a repeat and changes nothing, even if the first
  claim has already been decided.
- CORS is enabled only in the Development environment, and only for the Angular dev server's origin. The dev
  server's proxy means the browser normally does not need it.

### Failure handling in the watcher

- A file the pipeline cannot read (a scan, a corrupt PDF, an unsupported type) is moved to `local-inbox/failed/`.
- When the legacy mock or the API is down, the file stays where it is and is tried again on the next scan, up to
  five times, and then moved to `failed/`. This mirrors the blob trigger, which retries and then poisons a blob.
- Successful files move to `local-inbox/processed/`.
- The watcher polls once a second instead of using `FileSystemWatcher`: change events fire while a file is still
  being copied and are unreliable on network drives. The scan opens each file exclusively and skips it if another
  process is still writing.

## Consequences

Good:

- A reader can clone the repository and run the whole flow with only the .NET SDK (and Node for the UI), and CI
  runs the same pipeline code in its tests.
- Both extractors, both text readers and both hand-offs are unit-tested; the Azure ones against fakes.
- The Milestone 4 evaluation set can run the heuristic extractor in CI at no cost and compare it with a model.

Costs and risks:

- **The local extractors are not the production ones.** The heuristic extractor is a baseline: it reads labelled
  forms well and informal emails badly, and says so with low confidences. It will not read scans. A good result
  locally says nothing about how the model or Document Intelligence will perform.
- **The Azure implementations have not run.** `AzureDocumentIntelligenceExtractor`, `FoundryLlmClaimExtractor`,
  `ServiceBusClaimSink`, the API's `ServiceBusClaimConsumer` and the Function itself are compiled and tested with
  fakes only. There was no Azure subscription, no model access and no Functions Core Tools when they were written.
  The first real deployment will find problems.
- The HTTP route is a second transport to maintain. It is small, and both transports end in the same service.
- The in-memory review store is shared by nobody else and is lost when the API restarts. A durable store is
  future work.

## Alternatives considered

- **Azurite and the Functions Core Tools locally.** Closer to Azure, and the right choice for a team that works
  in Azure every day. Rejected for this milestone because it needs Docker or a Node-based emulator and the
  Functions host, which not every reader has, and it still would not run the model or Document Intelligence.
  Worth adding as a second local mode once there is a deployed environment to compare against.
- **Mock servers for Document Intelligence and the model.** Tests the HTTP shape, not the extraction. The
  heuristic extractor gives a working local flow, and the fake `IChatClient` in the tests covers the model's
  contract.
- **A file-system queue.** Reinvents Service Bus badly. The HTTP hand-off is simpler and fails visibly.
