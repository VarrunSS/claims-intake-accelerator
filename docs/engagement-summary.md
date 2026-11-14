# Engagement summary and handover note

> **Thames Mutual Insurance is a fictional customer.** This is the note a forward deployed engineer would leave with the customer at the
> end of the first phase, written for this portfolio project. Every figure was measured locally, on synthetic data, with no model and no
> Azure deployment. Where a result needs a real model or a real tenant, it says so.

## Problem

Thames Mutual's commercial property and liability claims arrive as emails, PDF forms and scans. Handlers read each one and re-key it, which
they estimate takes 10 to 20 minutes for a straightforward claim, and typing errors (policy number, loss date) come back later as rework
([discovery](00-discovery.md)). The goal was to put a pre-filled claim, checked against the policy wording, in front of a handler, with a
person approving every write to the claims system.

## What was built

A pipeline that takes a document, extracts six fields with a confidence for each, looks up the policy, validates the claim, searches the policy
wording for the clauses that matter, and recommends a route (straight to a handler, or senior review) with the clauses quoted. A review API and
an Angular UI show the source text beside the fields. Approval writes to the legacy system once, safely retried. Every tool call and every decision
is in an audit trail. The same code runs locally with no cloud account and on Azure through configuration, and Bicep and an `azd` project describe
the Azure side. Design decisions are in [ADRs 0001 to 0011](../README.md#documentation).

## Results against the success metrics

Measured on the 40-case held-out test split of 58 synthetic claims, by the offline heuristic extractor and rules assessor
([report](../evals/results/baseline/report.md)). The data was written by the same author as the extractor, so these figures flatter it.

| Metric ([definition](01-success-metrics.md)) | Target | Measured locally | Reading |
|---|---|---|---|
| M1 Handler time per claim | 5 min or less | not measured | Needs real handlers |
| M2 Field F1, six fields | 0.95 | 0.969 | Met on synthetic data. Cause of loss is weakest, 0.885 |
| M2 Policy number, loss date | 0.98 | 0.985, 0.985 | Met on synthetic data |
| M3 Claims needing no data repair | 60% or more | 70.0% | Met. The set contains about 27% of claims built to be refused |
| M4 Cost per claim, platform | £0.60 or less | £0.00 | Not meaningful: no model ran |
| M5 End-to-end p95 | 90 s or less | 2.6 ms | Not meaningful: no model, no network |
| G2 Passed with no flag but wrong | 1% or fewer | **5.9%** (1 of 17) | **Not met.** One case, but over the guardrail |
| G1 Writes without a recorded approval | 0 | 0 in tests | Enforced in the domain and tested, not observed in production |

Also measured: route accuracy 87.5% (35 of 40); issue precision 0.833 and recall 0.909; all 32 citations cited a real clause with its exact text
(by construction for the rules assessor, which can only cite what the search returned). Email documents are the weak spot: 58.3% route accuracy over 12 cases.

## Risks and known limitations

- **No real-model result.** The model comparison harness is built and tested on scripted fakes. The committed comparison says "not yet run against real models", and no model is recommended ([ADR 0008](adr/0008-model-selection-and-tuning.md)).
- **Synthetic data.** Real claims will score lower. The eval dataset, extractor and wordings share an author.
- **Nothing deployed.** The Bicep compiles and lints. It has not been through `what-if` or a deployment, the Function never started, and the Event Grid trigger never delivered an event. Roles being enough at run time is unproven. See [verification status](verification-status.md).
- **In-memory review store (REL-01).** An API restart loses claims awaiting review, and the API runs as one instance.
- **Public endpoints (INF-03).** Entra ID is the only gate. Private endpoints need Service Bus Premium, about £550 a month ([ADR 0011](adr/0011-network.md)).
- **Prompt injection.** Defences are in code (wording bound in the pipeline, citation guard, a rules route floor the agent cannot lower, JSON-encoded untrusted text) and proven against scripted models. Prompt Shields is a no-op seam (AI-03). Hidden text in a PDF is not detected (AI-04).
- **Large documents.** They are cut to 200 KB and flagged, not stored by reference (REL-02).
- **Authentication.** Token validation and app roles were tested with test-signed tokens, not a real tenant.

## Recommended next steps

1. **Durable store (REL-01).** Table Storage or Cosmos DB with ETags, so a restart loses nothing, then scale the API beyond one instance.
2. **First deployment** to a sandbox subscription, following the [runbook](runbook-deploy.md): two Entra app registrations, two provisioning passes, then confirm the role matrix, the blob trigger, one trace in Application Insights and the alert queries. Expect fixes.
3. **Real-model comparison run**: one command with a Foundry project ([runbook](runbook-model-comparison.md)), then decide with the written rules, and only then think about prompt changes or fine-tuning.
4. **Real evaluation data**: 100 anonymised claims from the customer to replace the synthetic split, and a handler-time study to measure M1.
5. **Prompt Shields** (Azure AI Content Safety) behind `IPromptAttackDetector`, and Defender for Storage for malware scanning.
6. **Private networking** if the customer's policy requires it, and the second-approver rule above a financial limit once discovery question 6 is answered.
7. **A deployment workflow** with OIDC federation, and first runs of the `iac`, `sbom`, `dependency-review` and CodeQL jobs ([GitHub setup](github-setup.md)).

## How I used AI tooling

Built with Claude Code, with the work split across Claude models by difficulty. After Milestone 5, a separate Claude Opus session reviewed the
architecture and security of the code as written. It raised 42 findings (1 critical, 9 high, 19 medium, 13 low), and Milestone 6 worked through
them: 35 are fixed, with tests offline or in the Bicep, and 7 remain open or documented (listed in the [review](reviews/2026-11-architecture-security-review.md)).
The review did not run anything on Azure, and neither did the fixes. Every figure in this note comes from a command run in this repository, not from a model's recollection.
