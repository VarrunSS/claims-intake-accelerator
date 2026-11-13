# Discovery: claims intake at Thames Mutual Insurance

> **Thames Mutual Insurance is a fictional customer.** This repository is a portfolio reference
> implementation. Every person, number and policy in it is invented, and the figures below are illustrative.
> They are written as they would stand at the end of week 1 of a real engagement.

Status: draft after week 1. Date: 13 November 2026. Next review: end of week 2.

## 1. Summary

Thames Mutual is a UK commercial insurer. Its commercial property and liability claims arrive as emails,
PDF forms and scans. Handlers read each one and re-key it into the claims system before any real claims
work starts. This takes time, introduces typing errors and leaves claims waiting in a shared inbox.

We propose a pipeline that reads each document, extracts the claim fields, checks them against the policy,
and puts a pre-filled claim in front of a handler. The handler checks it against the original document and
approves or rejects it. Nothing is written to the claims system without that approval.

## 2. Problem

- New claims are read and re-keyed by hand. Handlers estimate 10 to 20 minutes for a straightforward claim.
- Documents are inconsistent: broker emails with attachments, scanned forms, photographed letters, and
  forwarded chains where the policy number is in the third message.
- Mistakes at keying (policy number, loss date) surface later as rework: wrong policy attached, loss date
  outside the period of cover, or a duplicate of a claim already open.
- Claims wait in a shared inbox. There is no measure of how long, because the clock only starts when a
  handler opens the email.

## 3. Users

| User | What they do today | What they need |
|---|---|---|
| **Claims handler** (about 14 people across two teams) | Reads the notification, finds the policy, keys the claim, chases missing information. | A pre-filled claim, shown beside the source document, with problems flagged. A quick way to correct a field, then approve or reject. |
| **Team lead** (2) | Allocates claims from the shared inbox, watches for aged items, signs off larger claims. | A review queue ordered by age, with counts of flagged claims, so work can be allocated and ageing seen. |
| **Claims systems owner** (IT) | Runs the legacy claims system and its nightly batch. | No new way to write data that bypasses the system's own checks. Idempotent writes. A clear contract. |
| **Data protection officer** | Owns the UK GDPR record of processing and the DPIA. | Evidence of where data is processed, who can see it, and that a person makes the decision. |

Brokers and policyholders do not use the system. They keep sending claims as they do now.

## 4. Current process

1. A notification arrives in the shared claims mailbox (about 60% of volume), through the broker portal as a
   PDF (25%) or by post and scan (15%).
2. A team lead or handler picks it up and reads it. Attachments are opened one by one.
3. The handler finds the policy in the legacy system using the policy number, or the insured's name if the
   number is missing or wrong.
4. The handler checks the loss date falls inside the period of cover and searches for an existing claim for the
   same loss.
5. The handler keys the claim into the legacy system and files the documents.
6. Missing information is requested from the broker by email. The claim waits.

Volumes from the sample the customer shared (30 claims) and their management information:

| Measure | Value |
|---|---|
| New commercial claim notifications | about 1,400 a month |
| Notifications with a missing or unreadable policy number | about 1 in 6 in the sample |
| Notifications that are duplicates of an open claim | about 1 in 15 in the sample |
| Handler time from opening to keyed claim, straightforward case | 14 minutes median (30-claim time-and-motion) |

## 5. Pain points

1. **Re-keying.** Most of the 14 minutes is reading and typing, not judgement.
2. **Late discovery of problems.** Policy number, loss date and cover problems are found after keying.
3. **Duplicates** are found by memory or by searching the legacy system by name, which is unreliable.
4. **No ageing visibility** before a handler opens the email.
5. **No audit of why** a handler took a decision: the legacy system records the result, not the evidence.

## 6. Scope

In scope for the pilot: commercial property and liability first-notification-of-loss (FNOL) documents in
English, up to 10 pages, as PDF, image or `.eml`.

Out of scope: claims payments and reserving, motor and personal lines, handwriting recognition beyond what the
OCR service gives us, replying to brokers, and changes to the legacy system itself.

## 7. Constraints

| Constraint | What it means for the design |
|---|---|
| **UK GDPR and the Data Protection Act 2018** | Claim documents hold personal data, and sometimes health data (injury claims). We need a lawful basis, data minimisation, a retention period and a DPIA before real data is used. Logs and telemetry must not contain claim content. |
| **Automated decisions (UK GDPR Article 22)** | No decision with legal or similarly significant effect is made by the system alone. A handler decides every claim. See [ADR 0002](adr/0002-human-approval-for-writes.md). |
| **Data residency: UK South** | Storage, queues, the document service and the model deployment must be in UK South. Anything that would send data outside the UK is a blocker until the DPO agrees otherwise. See open question 1. |
| **Human in the loop for writes** | The legacy claims system is written to only after explicit handler approval. |
| **No secrets in code or settings** | Managed identity for service-to-service calls. See [ADR 0003](adr/0003-managed-identity-no-keys.md). |
| **Legacy system is not changed** | We integrate through its existing API surface. It accepts a claim and a policy lookup. It does not de-duplicate on its own, so writes must be idempotent. |
| **Existing Microsoft estate** | Entra ID for staff sign-in, Azure subscriptions already in place, .NET and Angular skills in the customer's IT team. |

## 8. Assumptions

These are the things we are working on until someone tells us otherwise. Each is tied to a question below.

- A1. Eighty per cent of notifications are in English and under 10 pages (to be confirmed from a larger sample).
- A2. The legacy system can be called over HTTPS from Azure with a service identity, and it can store an
  idempotency key or reject a repeat (Q3). In this repository it is imitated by `ClaimsIntake.Legacy`.
- A3. A handler will accept a pre-filled claim if they can see where each value came from in the document.
- A4. Policy period and policy number are enough for the first set of checks. Policy wording and exclusions
  come in Milestone 3.
- A5. The customer's current handling data is good enough to set a baseline (Q5).
- A6. Retention of source documents follows the existing claims file retention schedule.

## 9. Open questions

| # | Question | Why it matters | Owner | Needed by |
|---|---|---|---|---|
| 1 | Is the chosen model available in UK South, or in a UK-only data zone? If not, is processing elsewhere in the UK or EU acceptable to the DPO? | Decides model choice and may change the architecture. | Data protection officer, FDE | End of week 2 |
| 2 | Does the DPIA cover sending document text to a hosted model, and what is the retention for prompts and outputs at the provider? | Real claims cannot be used until this is answered. Synthetic data only until then. | Data protection officer | Before any real data |
| 3 | What does the legacy `POST /claims` do when it receives the same claim twice? Can it take an idempotency key? | Retries and replays could create duplicate claims. | Claims systems owner | End of week 2 |
| 4 | Which fields must be present before a claim can be created, and which are optional? | Drives the data-quality rules and what counts as "needs a human". | Team leads | End of week 2 |
| 5 | Can we have 100 historic claims, anonymised, with the keyed values? | Needed for a baseline and an evaluation set. We use synthetic claims until then. | Team leads, DPO | Week 4 |
| 6 | Who can approve a claim above a financial limit? Is a second approver needed? | Changes the approval flow. | Head of claims | Week 3 |
| 7 | How do handlers sign in to the review tool: Entra ID groups, or an existing role model? | Needed for audit and for who can approve. | Claims systems owner | Week 4 |
| 8 | What happens to a claim that is rejected: is the broker told, and by whom? | Out of scope today, but affects the status model. | Head of claims | Week 4 |
| 9 | Is a disaster-recovery region (UK West) required for the pilot? | Affects cost and the region list. | Head of IT | Week 6 |
| 10 | Does the Consumer Duty reach any of these claims (for example sole traders)? | Could change the handling rules for some claims. | Compliance | Week 4 |

## 10. Risks

| Risk | Likelihood | Effect | Response |
|---|---|---|---|
| Extraction is wrong and the handler does not notice | Medium | Wrong data written to the claims system | Show source spans beside each value. Flag low confidence. Measure and report "wrongly passed" claims. |
| Model not available in UK South | Medium | Delay or architecture change | Raise now (Q1). Keep the agent behind an interface so the model can change. |
| Handlers do not trust the output | Medium | Low adoption, no time saved | Involve two handlers in each demo from Milestone 2. Report accuracy on their own claims. |
| Legacy system cannot make writes safe to retry | Medium | Duplicate claims | Idempotency key in the contract (Q3). Duplicate check before every write. |
| Prompt injection in an email or PDF | Medium | The agent is steered to a harmful tool call | The agent cannot write to the legacy system. Only an approved handler action can. See ADR 0002. |

## 11. Next steps

1. Go through the 30-claim sample with two handlers and agree the field list (Q4).
2. Agree the legacy contract and idempotency with the systems owner (Q3).
3. Raise Q1 and Q2 with the DPO this week.
4. Milestone 2: one claim PDF through to the review screen, demonstrated to the handlers.
