# Security posture

What is in place, and where each claim is tested or why it is not. Milestone 6 part A added the first six items; part B the next block.

In place:

- **Authentication on every endpoint** except the health probes: Microsoft Entra ID JWT bearer through
  Microsoft.Identity.Web, a fallback policy that requires a signed-in caller, no start-up without the Entra settings. Tests
  send test-signed JWTs through the real validation pipeline: 401 without a token, for a wrong audience, an expired token or a
  wrong key; 403 for a wrong role. ([ADR 0009](adr/0009-identity-roles-and-segregation-of-duties.md))
- **App roles and segregation of duties.** `ClaimsHandler` and `SeniorHandler`. A claim routed to senior review can be
  approved only by a senior handler, enforced in the domain (`Claim.Approve`) and tested for every route and role pair and
  end to end. The reviewer is the token's `oid`, never the body. Service identities use app-only roles
  (`Claims.Write`, `Claims.Read`, `Policies.Read`, `Intake.ReadHistory`) and cannot act as handlers.
- **Every decision is audited** (approved, rejected, submitted, submit failed) with the actor's `oid`, role and route,
  with deterministic row keys and upserts, so a retry or redelivery cannot duplicate or lose one. Guardrail G1 is a test.
- **Agent hardening.** The wording and policy number are bound in code (the model cannot choose another wording), the
  citation guard rejects a clause from another wording, the rules assessor is a route floor the agent cannot lower, untrusted
  text is JSON-encoded in the prompts, the model gets a 24-call budget and no insured name. Scripted-model tests for each
  attack, and eval cases tagged `injection`.
- **No silent loss, app-code part.** Shared limits live in the Domain; an over-long document or trail is cut and flagged for
  a person instead of dead-lettered; the Service Bus hand-off is cut to under 200 KB; approve and reject share one per-claim gate.
- **PII scrubbing.** Spans and logs use the claim id, never the blob name, and HTTP spans carry route templates, not raw paths.
- **A dev sign-in that cannot exist outside Development.** Five layers, each tested (ADR 0009 section 7). The local hand-off
  route exists only in Development and needs an app token.
- **Per-app identities and a role matrix (Milestone 6b).** The Function, the API and the legacy mock each have their own
  user-assigned identity. Roles are scoped to the queue, container or account: the Function is Sender on the review queue, the API
  Receiver only, the legacy mock holds no Azure role, the audit table has an account of its own. `infra/role-matrix.expected.json` is the
  contract and `scripts/role_matrix.py` (14 Python tests, including "add Sender to the API fails") asserts it against the compiled
  ARM, in CI and locally. Publishing credentials are off, HTTPS only, TLS 1.2 and FTPS disabled on all three apps; the queue has
  duplicate detection; the inbox has a lifecycle policy ([ADR 0010](adr/0010-retention.md)); the Event Grid system topic and
  subscription have dead-lettering and an alert, with the `blobs_extension` key exception recorded ([ADR 0003](adr/0003-managed-identity-no-keys.md)).
- **No keys or connection strings.** Shared-key access is off on storage, local auth on Service Bus, Search, Foundry, Log Analytics
  and Application Insights, and application settings hold only resource names ([ADR 0003](adr/0003-managed-identity-no-keys.md)).
  The one exception is the Event Grid webhook key (ADR 0003 addendum). No Azure Policy enforces any of this; the templates do.
- **Azure Monitor OpenTelemetry (Milestone 6b).** API, Function (OpenTelemetry mode) and optionally the watcher export with managed
  identity; `DisableLocalAuth` is on. A W3C `traceparent` travels in the Service Bus message properties: a test proves the Function-side
  and API-side spans share one trace id through a fake sender and receiver, and the local smoke run showed one trace id from the watcher
  to the API. Metrics: time to review queue (histogram), claims stuck in Approved (gauge), dead letters, route distribution; token usage comes from the chat client's meter.
  Readiness reflects the Service Bus consumer. Alerts and a workbook are in Bicep. **Ingestion, the alert queries and the workbook were never run against Azure.**
- **Input limits and API hygiene (Milestone 6b).** 50-page limit, 30 s processing deadline and a single PDF parse; magic-byte
  checks against the claimed type; a 64 KB default request body limit (8 MB on the intake route only); rate limits per caller with
  429 and RFC 7807 problem details everywhere; an OpenAPI document with the bearer scheme and required roles, committed in
  `docs/openapi/review-api.v1.json` and checked for drift by a test.
- **Supply chain (Milestone 6b).** Actions pinned by SHA, least-privilege permissions per job, Dependabot, CodeQL, dependency review,
  `npm audit`, an SBOM artifact, central package management with lock files restored `--locked-mode`, no prerelease package in `src/`
  (`Azure.AI.Inference` beta and its Extensions.AI adapter stay in `evals/` only, on purpose). Secret scanning and push protection are repository settings: turn them on, they cannot be seen from the code.
- **IaC in CI.** `az bicep build`, `build-params`, `lint` (rules in `bicepconfig.json`), the role matrix and PSRule for Azure (`ps-rule.yaml`,
  non-blocking until it has run once).
- **Human approval for writes.** The agent can only propose. `propose_claim` records a proposal in memory; the
  tools are given no writer, so they cannot write (a test checks this) ([ADR 0002](adr/0002-human-approval-for-writes.md)).
- **UK South only.** The Bicep `location` parameter allows only `uksouth`.
- **Safe retries.** The legacy write needs an idempotency key. Reuse with a different body is refused. The API
  uses the claim id as the key, and a repeated approval returns the first result.
- **No document text in telemetry.** Spans and logs carry claim ids, stage names, durations, counts and confidence, and
  exception types, never text or field values. Tests check this.
- **Grounded citations.** A finding can only cite a clause the search returned in that run **from this claim's wording**, and
  the clause text shown is the wording's ([ADR 0006](adr/0006-policy-wording-rag.md)). Whether the clause means what the
  model says is for the handler.
- **Input handling.** Request bodies are validated and length-limited. Blob names are cleaned before logging. (Milestone 6b added the limits above.)
- **Defaults on the hosts.** HTTPS only, TLS 1.2 minimum, FTP off, Key Vault with RBAC, soft delete and purge protection,
  storage with no public blob access. Basic publishing credentials are off on all three apps (INF-04).
