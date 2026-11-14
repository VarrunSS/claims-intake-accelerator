# Evaluation report: test split, run baseline

40 synthetic claims, extractor `heuristic`, assessor `rules`. Fully offline: no model, no network, no credentials. **These are not real-model numbers**; the model-backed paths run with `--extractor llm --assessor agent --model <id>` (docs/runbook-model-comparison.md).

The data is synthetic and written by the same author as the extractor, so the numbers show where the heuristics are weak, not how a real insurer's mail would score. See evals/README.md.

## Gate: PASSED

| Metric | Required | Measured | Result |
|---|---|---|---|
| `auto_route_precision` | >= 0.9 | 0.933 | pass |
| `citation_exists` | >= 1 | 1.000 | pass |
| `citation_text_match` | >= 1 | 1.000 | pass |
| `cost_per_claim_gbp` | <= 0 | 0.000 | pass |
| `field_f1.cause` | >= 0.85 | 0.885 | pass |
| `field_f1.claimant_name` | >= 0.93 | 0.971 | pass |
| `field_f1.estimated_amount_gbp` | >= 0.95 | 0.984 | pass |
| `field_f1.loss_date` | >= 0.95 | 0.985 | pass |
| `field_f1.notification_date` | >= 0.97 | 1.000 | pass |
| `field_f1.policy_number` | >= 0.95 | 0.985 | pass |
| `field_f1_overall` | >= 0.93 | 0.969 | pass |
| `finding_precision` | >= 0.97 | 1.000 | pass |
| `finding_recall` | >= 0.82 | 0.850 | pass |
| `issue_precision` | >= 0.8 | 0.833 | pass |
| `issue_recall` | >= 0.87 | 0.909 | pass |
| `latency_p95_ms.process` | <= 250 | 2.600 | pass |
| `pipeline_errors` | <= 0 | 0.000 | pass |
| `route_accuracy` | >= 0.85 | 0.875 | pass |
| `silent_error_rate` | <= 0.15 | 0.059 | pass |

## Headline

| Measure | Value |
|---|---|
| Field F1, overall (micro, six fields) | 0.969 |
| Validation issues: precision / recall | 0.833 / 0.909 |
| Findings (kind + clause): precision / recall | 1.000 / 0.850 |
| Route accuracy | 87.5% (35 of 40) |
| Citation groundedness (clause exists / text matches) | 100.0% / 100.0% of 32 citations |
| Auto-routable | 37.5% (truth 47.5%) |
| Needs a person first | 62.5% |
| Cost per claim | £0.00 (local, no model) |
| Pipeline errors (threw instead of producing a claim) | 0 |

## Fields

Strict scoring: a wrong value counts as one false positive and one false negative. A field absent from both the document and the output scores nothing.

| Field | TP | FP | FN | Precision | Recall | F1 |
|---|---|---|---|---|---|---|
| policy_number | 32 | 0 | 1 | 1.000 | 0.970 | 0.985 |
| claimant_name | 33 | 1 | 1 | 0.971 | 0.971 | 0.971 |
| loss_date | 32 | 0 | 1 | 1.000 | 0.970 | 0.985 |
| cause | 27 | 0 | 7 | 1.000 | 0.794 | 0.885 |
| estimated_amount_gbp | 31 | 0 | 1 | 1.000 | 0.969 | 0.984 |
| notification_date | 34 | 0 | 0 | 1.000 | 1.000 | 1.000 |
| **overall (micro)** | 189 | 1 | 11 | 0.995 | 0.945 | 0.969 |

## Validation issues and findings

Issues exclude `LowConfidence`, which is the extractor's own doubt rather than a fact about the document. 32.5% of claims carried at least one low-confidence warning.

| | TP | FP | FN | Precision | Recall | F1 |
|---|---|---|---|---|---|---|
| Validation issue codes | 10 | 2 | 1 | 0.833 | 0.909 | 0.870 |
| Findings (kind + clause) | 34 | 0 | 6 | 1.000 | 0.850 | 0.919 |

## Route

Rows are the expected route, columns the route the pipeline chose.

| expected \ predicted | StraightToHandler | SeniorReview | None |
|---|---|---|---|
| StraightToHandler | 20 | 4 | 0 |
| SeniorReview | 1 | 15 | 0 |
| None | 0 | 0 | 0 |

SeniorReview precision 0.789, recall 0.938. StraightToHandler precision 0.952, recall 0.833.

## Citation groundedness

32 clauses were cited. 32 exist in the policy wordings and 32 quote the wording's text exactly. For the rules assessor this is 100% by construction: it can only cite a clause that `search_policy_wording` returned in the same run, and the text it shows is the wording's. The number becomes informative for the agent assessor in Milestone 5, where the citation guard is what keeps it at 100%.

## Auto-routable versus needs a person

A claim is *auto-routable* when the route is StraightToHandler and no blocking validation issue was raised: a handler can check and approve it. It ties to measure M3 in docs/01-success-metrics.md. The rest need a person first, and are the system correctly declining to pass them.

| | Pipeline | Ground truth |
|---|---|---|
| Auto-routable | 37.5% | 47.5% |
| No data repair needed (M3: no blocking validation error) | 70.0% | 72.5% |

Of the claims the pipeline auto-routed, 93.3% should have been (precision); 1 were auto-routed that needed a person. Of the claims that should be auto-routable, 73.7% were (recall).

Guardrail G2, silent errors: 17 claims raised no issue at all, and 1 of them had a wrong or missing critical field (5.9%). The target is 1% or fewer.

## Latency per stage

Milliseconds, from the pipeline's own spans, in process, on the machine that ran the evaluation (one warm-up claim discarded). `process` is the whole claim. The numbers vary a little from run to run and are not gated tightly.

| Stage | n | p50 | p95 | mean | cost per claim |
|---|---|---|---|---|---|
| input_quality | 40 | 0.1 | 0.3 | 0.3 | £0.00 |
| extract_text | 37 | 0.6 | 1.0 | 0.4 | £0.00 |
| input_quality_text | 37 | 0.0 | 0.1 | 0.0 | £0.00 |
| extract_fields | 34 | 0.1 | 0.6 | 0.1 | £0.00 |
| lookup_policy | 32 | 0.0 | 0.0 | 0.0 | £0.00 |
| validate | 34 | 0.0 | 0.2 | 0.0 | £0.00 |
| assess | 34 | 0.1 | 1.4 | 0.2 | £0.00 |
| handoff | 40 | 0.0 | 0.0 | 0.0 | £0.00 |
| process | 40 | 1.1 | 2.6 | 1.1 | £0.00 |

## By tag

A case can carry several tags, so the rows overlap. Small groups are noisy: read the count first. n/a means the group has nothing to score for that measure.

| Tag | Cases | Field F1 | Issue F1 | Finding F1 | Route accuracy |
|---|---|---|---|---|---|
| policy_typo | 1 | 0.800 | 0.000 | n/a | 100.0% |
| garbled | 1 | 0.833 | n/a | 1.000 | 100.0% |
| stated_vs_received | 1 | 0.909 | n/a | n/a | 0.0% |
| email | 12 | 0.939 | 0.500 | 0.667 | 58.3% |
| amount_words | 2 | 0.957 | n/a | 1.000 | 100.0% |
| duplicate_claim | 2 | 0.957 | n/a | 0.857 | 100.0% |
| multiple_dates | 2 | 0.957 | n/a | 0.667 | 50.0% |
| name_variant | 2 | 0.957 | n/a | 0.667 | 50.0% |
| uk_dates | 3 | 0.971 | n/a | 1.000 | 100.0% |
| missing_field | 4 | 0.974 | 1.000 | 1.000 | 100.0% |
| injection | 4 | 0.979 | n/a | 0.909 | 75.0% |
| pdf | 20 | 0.983 | 0.857 | 1.000 | 100.0% |
| clean | 3 | 1.000 | n/a | 1.000 | 100.0% |
| eml | 2 | 1.000 | n/a | 1.000 | 100.0% |
| exclusion | 6 | 1.000 | n/a | 0.947 | 83.3% |
| future_date | 1 | 1.000 | 1.000 | 1.000 | 100.0% |
| late_notification | 1 | 1.000 | n/a | 1.000 | 100.0% |
| ocr_noise | 1 | 1.000 | n/a | 1.000 | 100.0% |
| outside_period | 1 | 1.000 | 1.000 | 1.000 | 100.0% |
| duplicate_file | 1 | n/a | 1.000 | n/a | 100.0% |
| non_claim | 2 | n/a | 1.000 | n/a | 100.0% |
| quality_gate | 4 | n/a | 1.000 | n/a | 100.0% |

## Misses

Not listed for this split, by design: the test split is held out and is not used to tune anything. Per-case scores are in `results.json` for audit.

