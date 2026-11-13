# Success metrics

> **Thames Mutual Insurance is a fictional customer.** Baselines are illustrative figures from the
> fictional week-1 sample described in [00-discovery.md](00-discovery.md). Targets are proposals to agree with
> the customer, not results. Nothing here has been measured on a running system yet.

Status: proposed, for agreement with the head of claims in week 2.

## How to read this

A metric is only useful if everyone agrees how it is measured before the pilot starts. For each one below we
say what it means, where the baseline comes from, the target at the end of the pilot, how we measure it, and
who owns the number. Targets are for the pilot population: commercial property and liability notifications in
English, up to 10 pages.

## 1. Outcome metrics

| # | Metric | Baseline | Target at end of pilot | How it is measured |
|---|---|---|---|---|
| M1 | **Handler time per claim**: time from a handler opening the claim to a decision (approve or reject), median | 14 min (30-claim time-and-motion study) | 5 min or less | **Baseline:** observation with a stopwatch on the same 30 claims, repeated at 100 claims at the start of the pilot. **Pilot:** the API records when a handler opens a claim and when they decide. Median and 90th percentile reported weekly. Claims left open overnight are capped at the working day so a break does not inflate the figure. |
| M2 | **Field-level extraction accuracy**, on the critical fields (policy number, loss date, claimant, loss description, estimated amount) | Not applicable (people key them). A QA sample of keyed claims shows about 4% of fields needing later correction. | At least 95% of critical fields correct overall. At least 98% for policy number and loss date. | **Before go-live:** the evaluation set (50 synthetic claims in Milestone 4, then 100 anonymised real ones when available) scored by exact match after normalisation (dates to ISO 8601, amounts to pence, policy numbers upper-cased). Reported as precision, recall and F1 per field. **In pilot:** the difference between the extracted value and the value the handler approved, per field. |
| M3 | **Share of claims needing no data repair**: claims that pass every data-quality rule with no error, so a handler only has to check and approve | 0% (every claim is read and keyed by hand) | At least 60% | Count of claims whose validation result has no errors, divided by all claims received. The rules are in `ClaimsIntake.Domain` (`ClaimValidator`). Reported with the breakdown of errors by code (missing policy number, date in future, date outside policy period). The other claims are not failures: they are the ones the system correctly refuses to pass without a person. |
| M4 | **Cost per claim**: platform running cost plus handler time | About £11.20 (14 min at a loaded handler cost of £48 an hour, which is £0.80 a minute) | £5.00 or less in total, of which platform cost is £0.60 or less | **Platform:** Azure Cost Management for the resource group, plus token and page counts from telemetry, divided by claims processed in the period. **Handler time:** M1 multiplied by the same loaded rate. The loaded rate comes from the customer's finance team. |
| M5 | **End-to-end latency, p95**: from the document landing in the inbox to the claim showing in the review queue | No equivalent. Median wait for a handler to open an email is about one working day. | 90 seconds or less for documents of up to 10 pages | Time between the blob-created event and the claim being saved for review, taken from OpenTelemetry traces in Application Insights. Reported as p50, p95 and p99 for each document size band. |

## 2. Guardrails

These are not targets to improve. A breach stops the pilot until it is understood.

| # | Guardrail | Limit | How it is measured |
|---|---|---|---|
| G1 | Writes to the legacy system without a recorded handler approval | 0 | Audit table: every write references an approval record. A scheduled check compares legacy claim references with approvals. |
| G2 | Claims that passed validation with no flags but had a wrong critical field | 1% or fewer of passed claims | Handler corrections and a monthly blind re-check of 100 claims by a team lead. |
| G3 | Personal or claim content found in logs, traces or metrics | 0 | Automated scan of a week of telemetry for sample policy numbers and names, plus a manual spot check. |
| G4 | Documents or data processed outside UK South | 0 | Azure Policy restricting the allowed locations. Review of the resource list each release. |
| G5 | Duplicate claims created in the legacy system by a retry | 0 | Duplicate check on policy number, loss date and claimant over a rolling month. |

## 3. Supporting measures

Reported for diagnosis, with no target.

- Tokens in and out per claim, and pages processed by the document service per claim.
- Agent tool calls per claim, and the share that fail or retry.
- Share of claims where the handler changed at least one field, and which fields.
- Rejection rate and the reasons handlers give.
- Queue depth and age of the oldest unreviewed claim.

## 4. Evaluation gate in CI (from Milestone 4)

The pipeline will run the evaluation set on every change that touches the extraction agent or its prompts, and
fail the build if any of these thresholds is missed. The thresholds are the pilot targets above, set a little
lower for synthetic data and tightened once real claims are available.

| Check | Threshold |
|---|---|
| F1 on policy number and loss date | 0.97 or more |
| F1 on claimant, loss description, estimated amount | 0.92 or more |
| Policy citations that are supported by the retrieved wording | 0.90 or more |
| Mean cost per claim on the evaluation set | £0.60 or less |
| p95 latency on the evaluation set | 90 seconds or less |

## 5. Reporting

- A weekly one-page summary of M1 to M5 and the guardrails, in the pilot channel and to the head of claims.
- A short review at weeks 4, 8 and 12. A target that has been met with room to spare is raised. A target that
  looks wrong is renegotiated with the evidence, not quietly dropped.

## 6. What would make us stop

If at the week-8 review M2 is below 90% on critical fields, or any guardrail has been breached more than once,
we pause the pilot and either fix the cause or recommend not proceeding.
