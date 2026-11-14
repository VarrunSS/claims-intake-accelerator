# ADR 0001: Event-driven pipeline for document intake

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](../00-discovery.md).

- Status: accepted
- Date: 2026-11-13

## Context

Claim documents arrive at unpredictable times, in bursts (Monday mornings, after a storm), and take between a
few seconds and a minute or more to process: OCR, extraction by a model, policy lookup. The work is made of
independent steps that call external services and can fail or be throttled on their own. The customer wants
to know that a document has not been lost, that a failure can be retried without creating a duplicate claim,
and that a burst does not take the review screen down.

Requirements that shape the choice:

- A document must never be silently dropped. A failure must be visible and retryable. **This holds on the Azure path
  only with two caveats, stated plainly (Milestone 6b review):** the review store is in memory, so an API restart
  loses every claim awaiting review (REL-01, to be fixed with a durable store, which is not built; the API is pinned to one
  instance until then), and the claim-check pattern for very large documents is not built (the hand-off cuts the text
  below 200 KB and tells a person to read the original). What is visible: dead-lettered messages alert, Event Grid
  dead-letters, over-long and over-large inputs reach a person with a blocking issue.
- Handlers' review screen must stay responsive whatever the intake load.
- Processing is idempotent: the same document handled twice must not create two claims.
- Components are owned by different people later (intake, review, legacy integration), so they should be
  able to change and scale independently.
- Everything runs in UK South and authenticates with managed identity ([ADR 0003](0003-managed-identity-no-keys.md)).

## Decision

Use an event-driven pipeline built from managed Azure services:

1. A document lands in the **Blob Storage** `inbox` container.
2. **Event Grid** publishes a blob-created event.
3. An **Azure Function** (.NET isolated worker, Flex Consumption plan) with a blob trigger that uses the Event
   Grid source starts processing: OCR, then the extraction agent.
4. The Function runs the whole pipeline in process (no intermediate queue: the `claim-documents` queue the first
   milestone provisioned was never used and was removed in Milestone 6b) and writes the extracted claim to the
   **Service Bus** queue `claims-for-review`, which has duplicate detection on the claim id.
5. The **ASP.NET Core API** reads `claims-for-review`, stores the claim and serves the review screen.
6. After approval, the API writes to the legacy claims system with an idempotency key.

Service Bus is used for stage-to-stage messages because it gives dead-lettering, a maximum delivery count,
sessions and duplicate detection if we need them, and it supports Entra ID authentication with local keys
turned off.

Each message carries a document identifier. Every stage keys its work on that identifier, so a redelivered
message produces the same result.

## Consequences

Good:

- A burst makes the queue longer. It does not slow the review screen or lose documents.
- A failed message is retried, then dead-lettered where an operator can see it and replay it.
- Stages scale and are deployed separately. The agent stage can be replaced without touching intake.
- Every hop has a clear telemetry point for latency (success metric M5).

Costs:

- More moving parts than one web application, and eventual consistency: a document is not in the review
  queue the instant it lands.
- At-least-once delivery means every consumer must be idempotent. We will test this explicitly.
- Local development needs emulators or in-memory substitutes. The skeleton keeps the stores behind
  interfaces for this reason.
- The Event Grid subscription needs the Function App to exist before it can be created, so it is added after
  the first deployment (Milestone 2) rather than in the initial Bicep.
- The Flex Consumption plan only supports the Event Grid source for blob triggers. That is the one we want,
  but it means local runs need an Event Grid test event or a direct invocation.

## Alternatives considered

- **Upload through the API and process in the request.** Simple, but a slow model call ties up a web request,
  a failure loses the upload, and a burst hits the review screen. Rejected.
- **Polling blob trigger or a timer that scans the container.** Latency and cost grow with the container size,
  and it is not available on the Flex Consumption plan. Rejected.
- **Storage queues instead of Service Bus.** Cheaper and enough for simple fan-out, but no dead-letter queue
  with message properties, no duplicate detection and weaker ordering options. The extra cost is small.
  Rejected.
- **Durable Functions orchestration for the whole flow.** A good fit for multi-step flows with retries, and we
  may adopt it inside the Function stage in Milestone 2 or 3 if the steps grow. As the integration pattern
  between the stages it would tie review and legacy writes to the Function App. Deferred, not rejected.
- **Logic Apps or Power Automate.** Faster to start, but harder to test, version and review as code, and the
  customer's team will maintain this in .NET. Rejected.
