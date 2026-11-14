# ADR 0008: Model selection, prompt tuning, and when fine-tuning would be worth it

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](../00-discovery.md).

- Status: accepted for the method. **No model has been selected**: the harness is built and verified against scripted
  fakes, and has never been run against a real model, because this project's environment has no Azure AI Foundry
  deployment and no AI credentials. The recommendation in any committed `comparison.md` therefore says "not yet run".
- Date: 2026-11-14

## Context

Milestones 2 and 3 built a model-backed extractor (`FoundryLlmClaimExtractor`) and an agent assessor
(`AgentClaimAssessor`) behind `IChatClient` ([ADR 0004](0004-dotnet-agent-framework.md)), and Milestone 4 built a scorer
and a gate for the local pipeline ([ADR 0007](0007-evaluation-approach.md)). The customer's next question is which model
to run, and what it costs. Three things need deciding: how a model is chosen so that the choice can be repeated and
audited, how prompts are changed without losing track of what was measured, and whether to fine-tune.

## Decision

### 1. Models are chosen by a documented rule applied to measured rows, not by judgement

`ClaimsIntake.Evals compare` runs several configurations (a model and a prompt version) over the same split and writes
`comparison.md`. The recommendation in that file is **generated** by `Recommender` from the table. The rules, which are
also printed in every report:

1. **Candidates** are model configurations that were actually run. The heuristic baseline is the reference, not a
   candidate (it costs nothing and would always win). A registry entry that has not been run is not a candidate. Fake
   rows count only if no real model ran, and then the result is labelled a demonstration.
2. **Gate.** A candidate must pass every threshold in `evals/model-thresholds.json`: overall and per-field F1, issue and
   finding precision and recall, route accuracy, citation groundedness (clause exists, text matches), silent-error rate,
   no pipeline errors, a malformed-output rate of 5% or less, and a p95 latency ceiling of 60 s per claim. The quality
   bounds equal the offline baseline's gate, so a model must be at least as good as the heuristics to justify its cost.
   A metric that was not measured fails.
3. **Cost.** The candidate must have a measurable cost per claim: the model reported token usage on every call and the
   registry has a price.
4. **Pick** the cheapest passing candidate by £ per claim.
5. **Ties** (within £0.0001 per claim) go to the lower p95 latency (within 1 ms counts as equal), then the lower
   malformed-output rate, then the label alphabetically.
6. **No pick** if nothing passes. The report lists each candidate and the gates it failed.
7. **Caveats are attached, not applied.** A price marked verify-before-use is flagged on the pick.

Why cost first among the passing: quality is a gate and not a score because on 36 synthetic cases one claim is 3 points of
route accuracy, so ranking models by small quality differences would rank noise. Above the bar, the customer's
success metric M4 is cost per claim (target £0.60 or less, [01-success-metrics.md](../01-success-metrics.md)), and the
rule minimises it. If the team would rather pay for headroom, change rule 4 and this ADR together, never one alone.

What the rules do not do: they do not read a model's text, weigh a safety concern, or consider data residency or contract
terms. Those are checked by a person before a model is allowed into the registry.

### 2. A registry, no keys, and one client abstraction

`evals/models.json` lists the candidates: id, kind, endpoint, deployment, an `enabled` flag and a GBP price per million
input and output tokens with a source URL, an "as of" date and a status (`verified` or `verify-before-use`). Three kinds:
`azure-openai` (Azure OpenAI and Foundry OpenAI deployments), `azure-ai-inference` (Foundry model inference, for catalogue
models) and `foundry-anthropic` (Claude on Foundry, which uses the Anthropic Messages API and not chat completions). All
authenticate with `DefaultAzureCredential`; there is no key setting. Each is turned into an `IChatClient`
(`Microsoft.Extensions.AI`), so the extractor and the agent assessor are the production code, unchanged.

Facts checked on 14 November 2026, and what is still open:

- `Azure.AI.Inference` has only beta releases (1.0.0-beta.5) and its Extensions.AI adapter is a preview
  (10.0.0-preview.1). They are used in the evaluation project only and never by the Function.
- `Anthropic.Foundry` 1.0.0 and `Anthropic` 13.8.0 are stable and provide `IChatClient`.
- Microsoft's billing page for Claude on Foundry says usage is invoiced in Claude Consumption Units converted from
  Anthropic's per-model token rates, so a token price is not on an Azure price list.
- The Azure OpenAI price page was opened and its price cells did not render, so **no price in the registry is verified**.
  All three are placeholders marked verify-before-use. A cost ranking from them is arithmetic, not a finding.
- Nothing was run against a real service. Client construction is unit-tested with a credential that throws if asked for a
  token. Model availability in UK South, JSON-schema output support in the small model and tool calling in all three are
  unverified.

### 3. What a real run measures, and what is counted separately

Per call: tokens from `ChatResponse.Usage` (a model that reports none is flagged and not costed, never costed as zero),
latency, retries on HTTP 429 (exponential back-off, jittered, capped, with bounded parallelism), content-filter
refusals (an exception naming `content_filter`, or a `ContentFilter` finish reason), and calls that failed after retries.
Per reply: malformed (empty or not JSON), schema violations (JSON of the wrong shape, counted once per reply), and
citations or findings removed by the citation guard. Each is its own metric, not folded into accuracy, so a model that is
accurate when it answers but often does not answer is visible. Latency in the table is end-to-end per claim with the stated
parallelism, so it includes queueing and back-off.

### 4. Prompts are versioned files, and a new version is a hypothesis

Prompts live in `prompts/<stage>/v<n>.md`, are embedded in the pipeline assembly, and are selected by `--prompt` (or
`Intake:PromptVersion`). `results.json` records each version and the SHA-256 of its text (line endings normalised). A version
that has been measured is never edited; a change is a new version. v1 is what shipped. v2 is aimed at the four
weaknesses Milestone 4 found (emails, two-digit years, "burgled"-style causes, policy-number typos with a letter for a digit)
and is **not claimed to be better**: `prompts/README.md` states the hypothesis and how it will be judged (tune on the dev
split, confirm on the test split, per model).

### 5. Fine-tuning: not now, with criteria for when

Not implemented. Fine-tune a small model only if **all** of these hold:

1. **The gap is real and is the model's.** After prompt tuning (v2 or later) the best large model passes the gate and the
   small model does not, on the dev split and then the test split, and the failures are field errors or route errors, not
   retrieval or data-quality failures the model cannot fix.
2. **The saving is worth it.** The monthly volume times the cost difference between the large and the small model exceeds
   the cost of building and keeping the tuned model: training runs, hosting a tuned deployment (which often has an hourly
   charge even when idle), re-tuning on each base-model change, and a person's time. At pilot volumes this is unlikely; do
   the sum with real prices and the real volume.
3. **There is enough, and good enough, labelled data.** At least a few hundred reviewed claims per document type that matter,
   drawn from real handler corrections, with a test set that no tuning run has seen. 51 synthetic claims written by one
   author are neither enough nor independent, and tuning on them would only teach the model the generator's habits.
4. **The latency or residency need cannot be met otherwise** (for example a model that must run inside a boundary the large
   model cannot), or the volume makes per-token cost the dominant cost.
5. **The result stays measurable.** The tuned model enters the same registry, passes the same gate on a held-out split, and
   is re-measured whenever its base model or prompt changes.

Cheaper steps first, in this order: a better prompt, few-shot examples chosen by retrieval, a stricter output schema, a
router that sends easy claims to the small model and the rest to the large one, and tightening the data-quality gate.

### 6. Data a fine-tune would need, and UK GDPR

- **Data**: pairs of document text and the handler-approved fields (and route and cited clauses), taken from the review
  queue's approved and corrected claims. The correction is the label, so the audit trail already holds the ground truth.
  Include the cases handlers rejected and the non-claims. Split by claim, not by document, and keep a held-out set that is
  never used to train or to choose a checkpoint.
- **Personal data.** Claim documents hold names, addresses, policy numbers and sometimes health data. Training data is
  processing: it needs a lawful basis, a purpose that covers it, a DPIA update, and the data minimisation the discovery
  notes already require. Redact or pseudonymise before it leaves the claims system where the task allows (names to
  placeholders, dates kept as dates), and check that redaction did not remove what the model needs to learn from.
- **Special category data.** Health information in injury claims needs an Article 9 condition. The simplest course is to
  exclude those claims from training data.
- **Where it goes.** Tuning data is uploaded to the provider. It must stay in the UK (the discovery data-residency rule),
  with the provider's terms confirmed on retention, use for the provider's own training (it must be no), access by the
  provider's staff, and deletion. Open question 2 in the discovery notes (whether the DPIA covers sending text to a hosted
  model, and prompt and output retention) is a precondition, and is not answered.
- **The tuned model memorises.** A fine-tuned model can reproduce training text. Treat the tuned weights and any deployment
  as holding personal data: same access controls, same retention and deletion path, and a record of which claims were in
  which version, so a data-subject request can be answered.
- **Article 22 and the human approval** in [ADR 0002](0002-human-approval-for-writes.md) are unchanged: tuning does not move
  a decision from a handler to the model.
- **Until a DPIA says otherwise, synthetic data only.**

## Alternatives considered

- **Pick the model with the best accuracy.** Rejected: on 36 cases the differences between good models are inside the
  noise, and accuracy alone ignores the cost target. Accuracy is a gate, cost is the objective.
- **A weighted score of accuracy, cost and latency.** Rejected: the weights would be an opinion that decides the outcome,
  and the result is hard to explain to a customer. A gate and one objective can be read in a minute.
- **A hand-written recommendation.** Rejected by requirement: it cannot be repeated or audited, and invites reading the
  numbers the way we hope they come out.
- **Python and the Azure AI Evaluation SDK for the comparison.** Rejected for the reason in ADR 0007: the harness runs the
  same pipeline code in process. Model-graded metrics remain possible later.
- **Always create the Foundry account in Bicep.** Rejected: it is behind `deployFoundry` (default false), so `azd up` is
  unchanged and costs nothing until someone turns it on.

## Consequences

- A run can be repeated with one command and the report says how each number was obtained.
- The first real run will probably need threshold changes: the reliability bounds (malformed rate, latency) have never been
  measured against a model. Change them in the same change as the reason.
- The cost ranking is only as good as the registry prices. Verify them before acting on a pick.
- The harness is exercised end to end only on fakes. Real services will expose problems the fakes cannot (response shapes,
  usage reporting, rate-limit headers, a model that ignores the JSON schema). Expect the first real run to be a debugging
  session, and keep its output.
- Latency under bounded parallelism is a measure of the evaluation, not of a single request.

## Not verified

- Any behaviour against a real model or a real Foundry account, including authentication, the three client adapters, token
  usage reporting, 429 and content-filter behaviour, and structured output support.
- Every price. Model availability and quota in UK South.
- The Bicep for the Foundry account (`infra/modules/foundry.bicep`): never built or deployed (no `az` or `bicep` here).
- That prompt v2 is better. It has not been run.
