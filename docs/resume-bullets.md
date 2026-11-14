# Resume bullets

Three variants of one bullet, each 40 to 60 words. Pick the one that matches the job description, and keep the project's caveats: they are
what makes the numbers believable. Every number below was measured in this repository and is explained under the bullets.

## Variants

**A. Engagement and security (default for forward deployed or customer-facing roles)**

> Built an end-to-end claims-intake reference implementation (C#/.NET 10, Angular, Azure, Bicep) that turns messy documents into pre-filled claims with cited policy clauses, behind human approval. Independent review raised 42 findings; 35 fixed with tests. Offline eval on 40 held-out synthetic claims: field F1 0.969, route accuracy 87.5%. 846 .NET tests. IaC compiled, not deployed.

**B. Evaluation and model selection (for AI, RAG or evaluation roles)**

> Designed the evaluation for a claims-extraction pipeline: 58 synthetic claims with ground truth, field-level F1, route accuracy and citation checks, enforced as a CI gate. Offline baseline on 40 held-out claims: F1 0.969, route accuracy 87.5%, 32 of 32 citations grounded. Built a model-comparison harness with written decision rules; no real model run yet.

**C. Platform and security (for cloud, platform or security-minded roles)**

> Delivered an Azure reference implementation of a document-to-claim workflow (.NET 10, Agent Framework, AI Search, Bicep, GitHub Actions) with Entra ID roles, segregation of duties, per-component identities checked by a CI role matrix, and OpenTelemetry. 846 .NET and 60 UI tests pass. Bicep compiles and lints; not yet deployed.

## What each number refers to

| Number | What it is | Source |
|---|---|---|
| 846 .NET tests | Domain 116, API 208, Pipeline 378, Evals 144, all passing with 0 build warnings. 60 more UI tests (Vitest) and 14 Python tests | `dotnet test`, `npm test` |
| Field F1 0.969 | Micro-averaged over six fields (policy number, claimant, loss date, cause, amount, notification date), strict scoring, 40-case held-out test split, offline heuristic extractor and rules assessor, **no model** | [baseline report](../evals/results/baseline/report.md) |
| Route accuracy 87.5% | 35 of 40 claims sent to the right route (straight to a handler or senior review), same run | same |
| 32 of 32 citations grounded | Every cited clause exists in the wording and the quoted text matches. True **by construction** for the rules assessor; it says nothing about a model | same |
| 58 synthetic claims | The whole dataset: 18 dev, 40 test, written by the same author as the extractor | [evals/README.md](../evals/README.md) |
| 42 findings, 35 fixed | An independent Claude Opus review of the code (1 critical, 9 high, 19 medium, 13 low). 35 are fixed with offline tests or Bicep changes, 7 are open or documented. Nothing was verified on Azure | [review](reviews/2026-11-architecture-security-review.md) |
| Bicep compiles and lints, not deployed | `bicep build` and `lint` with Bicep 0.49.2, plus the role-matrix assertion. No `what-if`, no deployment | [infra/README.md](../infra/README.md) |

## Do not say

- That it was deployed, run on Azure, or run against a real tenant. It was not.
- Any accuracy, cost or latency for a real model. None was measured. The F1 and route figures are for the offline heuristic pipeline on synthetic data.
- "One-command `azd up`". The `azd` project and Bicep exist, but the first deployment needs two Entra app registrations and two provisioning passes ([runbook](runbook-deploy.md)), and it has never been run.
- "100% accuracy" for citations. It is 100% groundedness of the rules assessor's citations, by construction.
- "Production-ready". Say "production-grade design" only with the caveats: the review store is in memory and nothing is deployed.
