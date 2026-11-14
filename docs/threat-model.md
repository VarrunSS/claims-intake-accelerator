# Threat model (STRIDE)

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](00-discovery.md).

Scope: the data flow below, as built after Milestone 6a. Finding ids (SEC-01, AI-01 and so on) are from the
[architecture and security review](reviews/2026-11-architecture-security-review.md). Status values:

- **Mitigated (offline)**: a control in code, proven by a test in this repository.
- **Mitigated (Azure unverified)**: a control in code or configuration whose Azure behaviour has never run.
- **Open**: not fixed. Part B of Milestone 6, or later.

Nothing here has been run on Azure or against a real Entra ID tenant. "Tested" means an offline test with fakes or
test-signed tokens.

## Data flow and trust boundaries

```
 Broker / insured ──(1) document──▶ inbox blob ──▶ Function / watcher (pipeline) ──(2)──▶ model, Search, Document Intelligence
                                                        │  (3) app-only token            (4) hand-off
                                                        ▼                                   ▼
                                               legacy mock (policies, claims)       Service Bus / HTTP ──▶ Review API ◀──(5) user token── Handler (UI)
                                                                                                │ (6) app-only token
                                                                                                ▼
                                                                                          legacy mock (write)
```

1. **Untrusted input.** A document from outside. Its text, its name and its bytes are attacker-controlled.
2. **Pipeline to model and platform services.** Document text goes to a model. The model's output is untrusted.
3. **Pipeline to the legacy system and the review API** (reads): app-only Entra tokens.
4. **Hand-off** of a proposed claim to the review store.
5. **People to the review API**: Entra user tokens with app roles.
6. **Review API to the legacy system** (the only write): an app-only token with `Claims.Write`.

## Threats

### Spoofing

| Threat | Mitigation | Status |
|---|---|---|
| Anyone calls the review API (SEC-01) | JWT bearer through Microsoft.Identity.Web; a fallback policy requires an authenticated caller; the service does not start without Entra settings. Tests: 401 on every claim route without a token, for a wrong audience, an expired token, a wrong signing key | Mitigated (offline). Real tenant never used |
| Someone approves "as" another person (SEC-02) | The reviewer is the token's `oid`; a body that names one is a 400. Test: decision records the token's oid | Mitigated (offline) |
| Anyone writes to the legacy system or reads its claims (SEC-05) | App-only tokens with `Claims.Write` / `Claims.Read` / `Policies.Read`; user tokens refused even with the role. Tests: 401, 403, 200 | Mitigated (offline). The legacy mock is still deployed with a public host by the current Bicep (INF-01, part B) |
| A service identity acts as a handler, or a user calls a service-only route (SEC-06) | Handler policy refuses `idtyp=app`; app policies require `idtyp=app`. `/claims/history` needs `Intake.ReadHistory`. Tests | Mitigated (offline) |
| The dev sign-in leaks into a deployment | Five layers (ADR 0009 section 7): start-up environment check, Azure-host check, per-request check, Entra default, service-side `DevTokenHandler` refuses outside Development. Tests for each | Mitigated (offline) |
| A stray setting opens the local hand-off route (SEC-07) | Mapped only in Development, and needs `Intake.Submit`. Test: 404 in Production and Staging | Mitigated (offline) |

### Tampering

| Threat | Mitigation | Status |
|---|---|---|
| A document steers the model into a forged citation (AI-01) | Wording and policy number bound in code; no wording parameter on the search tool; `CitationGuard` rejects any clause not from this claim's wording; the text shown is the wording's. Scripted-model tests | Mitigated (offline). Never run against a real model |
| A document talks the model out of an exclusion, the route is lowered (AI-02) | The rules assessor runs beside the agent as a deterministic route floor: the agent can raise the route, never lower it; disagreement is noted and counted. Tests; eval cases tagged `injection` | Mitigated (offline) |
| Delimiter breakout in the prompt (AI-03) | Untrusted text is JSON-encoded in both prompts with angle brackets escaped. Tests: a `</claim>` cannot occur in the message | Mitigated (offline). Prompt Shields not integrated: the `IPromptAttackDetector` seam is a no-op locally and was never run against Azure AI Content Safety |
| Hidden text in a PDF (white-on-white, tiny fonts) steers the model or the handler (AI-04) | None yet. The handler sees the extracted text, not the rendered page | **Open** (documented known limitation) |
| An injected document makes the model pull another policy's data into a summary (AI-06) | Tools refuse another policy number; `lookup_policy` returns no insured name; a refusal routes to senior review | Mitigated (offline) |
| A handler approves a claim above their authority (SEC-03) | The rule is in `Claim.Approve`: `SeniorReview` needs `SeniorHandler`. Tests for every route and role pair; end to end 403 and 200 | Mitigated (offline) |
| A decision is made twice, or a reject races an approval (REL-03) | One per-claim gate for approve and reject; legacy write idempotent on the claim id. Tests with a blocked legacy writer | Mitigated (offline). The review store is in memory (REL-01, open) |
| A forged message on the queue (INF-01) | Sender role is shared by all apps in the current Bicep | **Open** (part B: identity split) |
| A tampered build or dependency (SC-01) | None beyond `contents: read` workflow permissions | **Open** (part B) |

### Repudiation

| Threat | Mitigation | Status |
|---|---|---|
| "I did not approve that" (SEC-04) | `decision.approved`, `.rejected`, `.submitted`, `.submit_failed` in the audit trail with `oid`, role, route, outcome, legacy reference; written before the legacy call. Guardrail G1 is a test | Mitigated (offline). Durable only with the Table Storage log, never run on Azure |
| An audit entry is lost or doubled on a retry (REL-05) | Deterministic row keys, upserts, the trail written before the claim. Test: a table that fails on the second append, then a redelivery | Mitigated (offline) |
| A decision is saved without its record, or the reverse | Audit and state are two writes, not a transaction | **Open** (known limit, fixed with the durable store in Milestone 7) |

### Information disclosure

| Threat | Mitigation | Status |
|---|---|---|
| Claim text, names or policy numbers in logs and spans (PII-01, PII-02) | Telemetry keyed by claim id, never the blob name; HTTP spans carry route templates (`UrlScrubber`). Tests: a blob name holding a person's name never appears in a log line or span tag; server and client spans carry `/policies/{number}` and `/claims/{id}` | Mitigated (offline). The Function host's own Application Insights collection is not scrubbed (part B) |
| The legacy list leaks claimant names (SEC-05) | `GET /claims?policyNumber=` returns only the fields the duplicate check needs. Test | Mitigated (offline) |
| The audit trail holds personal data (PII-04) | Reworded: pseudonymised (oid, policy numbers, dates, amounts; no names or free text), classified as personal data | Documented. Retention not implemented (PII-03, part B) |
| A public network surface (INF-03) | None in code | **Open** |
| Prompts or documents sent to a model provider | Out of scope for the code; see the customer's DPIA | Open (documentation) |

### Denial of service

| Threat | Mitigation | Status |
|---|---|---|
| A tool loop or many calls per turn (AI-05) | 24-call budget; failure becomes a senior-review assessment; the trail stays under the API limit. Test with 30 calls per turn | Mitigated (offline) |
| A long document or a 150-entry trail gets a claim dead-lettered (REL-04) | Limits defined once in the Domain; the pipeline cuts text, the API cuts text and trail and adds a blocking issue instead of refusing. Tests | Mitigated (offline) |
| A message over 256 KB poisons the Function (REL-02) | `ServiceBusClaimSink` cuts the text to under 200 KB and names the original. Test with a 300 KB claim | Mitigated (offline) for the size. The claim-check pattern is **Open** |
| PDF bombs, page counts, no rate limits (INP-01, API-01, INP-03) | None yet | **Open** (item 10) |

### Elevation of privilege

| Threat | Mitigation | Status |
|---|---|---|
| The agent writes to a system | `ClaimTools` has no writer, a constructor test checks it; `propose_claim` records in memory only | Mitigated (offline) |
| A compromised identity reaches everything (INF-01) | One identity for all apps in the current Bicep | **Open** (part B) |
| Role assignment mistakes in Entra (a person given an app-only role) | App roles for services are declared `Application`-only in the manifests, and the `idtyp` check is the second lock | Mitigated (design). Never applied to a tenant |
| Unoccupied or stuck claims bypass review (REL-08) | A claim the legacy system refuses stays `Approved` with no way back | **Open** |

## Residual risks, in order

1. The identity design has never met a real tenant. Test tokens are shaped like Entra's; the real ones are unverified.
2. The deployed infrastructure still has one identity and public hosts (part B).
3. The review store is in memory: a restart loses claims awaiting review (REL-01).
4. Hidden text and prompt injection are reduced, not removed: the route floor, the citation guard and the human are the
   controls, and the eval cases measure only the offline assessor.
