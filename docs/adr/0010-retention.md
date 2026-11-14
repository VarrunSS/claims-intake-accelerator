# ADR 0010: Retention of documents, claims, audit records and telemetry

- Status: accepted (Milestone 6b). **The periods are assumptions for the fictional customer (Thames Mutual), not legal advice.**
  A real deployment sets them with the customer's data protection officer and records them in the DPIA.
- Context: UK GDPR requires storage limitation: personal data is kept no longer than needed for its purpose (Article 5(1)(e)).
  The system holds claimant documents, extracted fields, handler decisions and telemetry (PII-03).

## Decision

| Data | Where | Period (assumption) | Why | Mechanism |
|---|---|---|---|---|
| Original document (PDF, email) | Blob `inbox` | **90 days** after last modification | A working copy. The record of the claim is the legacy claims system, which follows the customer's claims-file schedule (discovery A6). 90 days covers handler review, a complaint window and a re-run of extraction. | Storage lifecycle policy, `inboxRetentionDays` (default 90, minimum 30). Soft delete then keeps a deleted blob 7 days. **In Bicep; never run.** |
| Proposed-claim hand-off message | Service Bus `claims-for-review` | **7 days** time to live | Long enough to survive a weekend outage of the API; a claim older than that has already been dead-lettered. | `defaultMessageTimeToLive: P7D`, dead-letter on expiry |
| Dead-lettered messages | Dead-letter queue | **14 days**, then purged by a person | They carry the full document text, so they must not sit for ever. Service Bus has no expiry on dead letters, so this is a runbook step, with an alert while any exist. | Alert on `DeadletteredMessages > 0`; `docs/runbook-dead-letters.md` |
| Dead-lettered Event Grid events | Blob `eventgrid-deadletter` | Events name a blob, they do not hold its text. Reviewed with the dead letters, 14 days | Same | Runbook (no lifecycle rule yet) |
| Claim under review, handler decision | API store (in memory today) | Until the claim is decided, plus the legacy system holds the record | Today lost on restart (REL-01). The durable store (Milestone 7) must expire claims 90 days after decision. | Not implemented |
| Audit records (tool calls, decisions) | Table `auditlog`, its own account | **7 years** | Pseudonymised (ids, oid, route, outcome; no names or free text) but personal data when the insured is a sole trader. Kept for the claim file's limitation period (6 years in England and Wales) plus a year, so that a decision can be evidenced. | **No automatic expiry.** Table Storage has no TTL; a scheduled purge by partition is Milestone 7. Documented gap. |
| Telemetry (logs, traces, metrics) | Log Analytics and Application Insights | **30 days** | Operational use only; it holds claim ids, not claim content (tests). | Workspace `retentionInDays` 30 (the minimum), App Insights the same |

## Consequences

- Only the inbox, the queue and telemetry are enforced by configuration. Audit expiry and dead-letter purge are procedures
  until Milestone 7; this ADR says so rather than implying otherwise.
- A claim reopened after 90 days must be re-submitted: the original is in the legacy system, not the inbox.
- Changing a period is a parameter or a one-line change, reviewed with the DPO. The numbers above need that review.
