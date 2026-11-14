# Evaluations

> Thames Mutual Insurance is a fictional customer. Every claim in `dataset/` is synthetic.

This folder measures the intake pipeline against claims whose right answer is known. It answers three questions: how
often are the fields right, how often does the claim go to the right place, and are the clauses it cites real. It runs
offline in a few seconds, and in CI it fails the build when a number drops below `thresholds.json`
([ADR 0007](../docs/adr/0007-evaluation-approach.md)).

**What is measured today is the local pipeline only**: the heuristic extractor and the rules assessor. There are no
Azure credentials in this project's environment, so no model has been evaluated and **no real-model number exists yet**.
Milestone 5 added the harness that will run this same dataset and scorer against real models (`--extractor llm --assessor
agent --model <id>` and `compare`, below). It is tested end to end against scripted fakes and has never been run against a real
model: see [Model comparison](#model-comparison-milestone-5) and [ADR 0008](../docs/adr/0008-model-selection-and-tuning.md).

## Run it

```bash
dotnet run --project evals/ClaimsIntake.Evals -- --split dev                       # the split you may look at
dotnet run --project evals/ClaimsIntake.Evals -- --split test --thresholds evals/thresholds.json
python evals/generate_dataset.py                                                   # regenerate the dataset (needs reportlab)
```

| Option | Meaning |
|---|---|
| `--split dev\|test\|all` | Which cases to run. Default `test`. |
| `--thresholds <file>` | Exit 1 if any metric is outside its bound. |
| `--run-id`, `--out` | Writes `<out>/<run-id>/report.md` and `results.json` (default `evals/results/<UTC time>-<split>`). |
| `--extractor heuristic\|llm`, `--assessor rules\|agent` | `heuristic` and `rules` run offline and are the default. `llm` and `agent` call a model and need `--model`. |
| `--model <id>`, `--prompt <version>` | A model from `models.json` (DefaultAzureCredential), and the prompt version (default `v1`). |
| `--registry <file>`, `--parallelism <n>` | The registry (default `evals/models.json`); claims in flight and the cap on model calls in flight (default 1 offline, 4 for a model). |
| `--dev-fakes` | Dev only. Allows `--model fake-oracle` and `fake-noisy`. See below. |

`compare` is a second command: `dotnet run --project evals/ClaimsIntake.Evals -- compare [--models a,b] [--prompts v1,v2]`.

Exit codes: 0 all thresholds held, 1 a threshold was missed, 2 the run could not be done. Run results are git-ignored;
only `results/baseline/` is committed.

### Why a .NET console and not Python

The plan sketched Python. A .NET console reuses the pipeline in process: the same `ClaimIntakePipeline`, extractor,
assessor, policy search and input-quality gate that run in the Function, with no second implementation to drift, no
HTTP hop and no serialisation. The latency numbers come from the pipeline's own OpenTelemetry spans. The cost is that
the Azure AI Evaluation SDK (Python) is not used; Milestone 5 can call it for model-graded metrics if they are wanted.
Python is used only where it is the better tool: `generate_dataset.py` writes the PDFs with reportlab, as the sample
generator already does.

## The dataset

`dataset/` holds 51 cases. Each is a document (`.pdf`, `.txt` or `.eml`) and `<id>.expected.json`:

```json
{
  "id": "c30-multiple-dates-email", "file": "c30-multiple-dates-email.txt", "split": "test", "tags": ["email", "multiple_dates"],
  "context": { "receivedAt": "2026-11-05T13:07:00Z", "existingClaims": [], "duplicateOf": null, "maxDocumentBytes": null },
  "expected": {
    "fields": { "policy_number": "...", "claimant_name": "...", "loss_date": "2026-11-03", "cause": "water",
                "estimated_amount_gbp": 4100.0, "notification_date": "2026-11-05" },
    "issueCodes": [], "findings": [ { "kind": "CoverageClause", "clauseRef": "3.2" } ], "route": "StraightToHandler"
  }
}
```

`context` is what the pipeline is told besides the document: when it was received, claims that already exist (for the
duplicate check), another case whose file counts as already received (for the duplicate-file check), and, for the
oversized case only, a smaller size limit so that a 10 MB file need not be committed.

### How the cases are made

`generate_dataset.py` writes a **specification** for each case (policy, cause, dates, amount, how the document is
written) and renders the document from it. The expected values are computed from the same specification using tables
copied from the wordings: which clause insures which cause under which wording, which exclusion clause applies, the days
each wording allows for notification. **The pipeline is never run to make the ground truth.** A fixed seed and a random
stream per case make the output byte-for-byte repeatable; adding a case does not change the others. Tests check that
every expected clause exists in the right wording and section, that expected lateness agrees with the dates and the
wording's period, and that expected policy-period issues agree with the seeded policies.

### Difficulty tags

| Tag | What it covers |
|---|---|
| `clean`, `pdf`, `email`, `eml` | A tidy form; the document type. |
| `missing_field` | No policy number, no loss date, or no amount. |
| `amount_words`, `uk_dates` | "twelve thousand pounds", "GBP 15,200"; `3rd Nov 2026`, `03/11/2026`, `03/11/26`, a weekday. |
| `policy_typo` | A policy number that does not exist (`TM-CP-10002`, `TM-CP-1OO003`). Expected as written, with `UnknownPolicy`. |
| `garbled`, `ocr_noise` | Fragments scattered over the page, a word split in two, look-alike characters. |
| `multiple_dates` | Policy start date, a previous claim, a date the problem was discovered, as well as the loss date. |
| `exclusion` | Flood on a wording that excludes it, wear and corrosion, premises unoccupied for more than 30 days. |
| `late_notification`, `stated_vs_received` | Notified later than the wording allows; a document that states an earlier notification date than the day it was received. |
| `duplicate_claim`, `duplicate_file` | The same loss as an existing claim; the same file received twice. |
| `non_claim`, `quality_gate` | A marketing email, a French and an internal email; a blank scan, a one-line note, a corrupt PDF, a password-protected PDF, an oversized file. |
| `name_variant`, `outside_period`, `future_date` | `and` for `&` and `Limited` for `Ltd`; a loss outside the policy period; a loss after the notification date. |

A case carries several tags, so the per-tag rows overlap.

### The dev and test split (read this before changing the extractor)

| Split | Cases | Use |
|---|---|---|
| `dev` | 15 | Look at it. Read its misses. Improve the extractor and rules against it. |
| `test` | 36 | Held out. **Do not tune against it.** It is what CI gates on and what the headline numbers come from. |

The split is stratified by hand in the generator so that dev covers most tags. Discipline:

- The dev report lists what was wrong in each case. The test report does not (per-case scores are in `results.json`, for
  audit, not for tuning).
- Improvements to the extractor were made against dev failures only. Two were made: reading an amount written in words,
  and reading the insured from a sentence such as "I am the office manager at ...". Dev field F1 went from 0.943 to 0.965
  (claimant 0.833 to 0.917, amount 0.870 to 0.917). The test split was run for the first time after that.
- Two-digit years (`03/11/26`), the garbled layout and the look-alike characters in dev were **not** fixed: none is a
  weakness the dev split alone motivates, and fixing them would be tuning to a case written to be hard.
- One test case (`c38`) was inspected after the first test run, to confirm that a route miss was an extractor miss and
  not a defect in the date fix. It was: the description used a word ("burgled") the loss keywords do not contain. Nothing
  was changed in response.
- When real claims exist, they are a third split, and the synthetic test split stops being the number that matters.

## What is measured

Every case is run with its own clock fixed at its `receivedAt`, its own duplicate-file record and its own existing claims,
so a result never depends on the day the evaluation runs, the order of the cases, or any service.

### Fields

Six fields: `policy_number`, `claimant_name`, `loss_date`, `cause`, `estimated_amount_gbp`, `notification_date`. Each is
compared after normalisation:

| Field | Rule |
|---|---|
| policy number | Exact after trimming. No case folding and no repair: `tm-cp-100001` and `TM-CP-10001` are wrong. |
| claimant | Case-insensitive; `&` equals `and`; `Limited` equals `Ltd`; punctuation dropped; spaces collapsed. Nothing cleverer. |
| loss date, notification date | ISO 8601 and UK day-first formats (`3 November 2026`, `03/11/2026`). Anything else is not a date. |
| amount | In pence, from `£12,500`, `8,750.00` or `12500`. Number words and `GBP 100` are not read, so they are misses. |
| cause | The class (fire, flood, theft, storm, impact, refrigeration, breakdown, water) of the extracted loss description, using the same keyword classifier the rules assessor uses. |
| notification date | `ProposedClaim.NotifiedOn`: the date the document states, otherwise the date it was received. |

Scoring is strict. Both present and equal is a true positive. Both present and different is one false positive **and** one
false negative. A value where none is expected is a false positive; none where one is expected is a false negative; none on
both sides scores nothing. Precision, recall and F1 are reported per field and **micro-averaged** overall (counts summed
across fields, so a field that is present more often weighs more). A ratio with a zero denominator is `n/a`, never 0 or 1.

### Validation issues, findings and route

- **Issues**: the set of validation issue codes per claim, micro-averaged precision and recall. `LowConfidence` is
  excluded: it is the extractor's own doubt, not a fact about the document. The share of claims carrying one is reported.
- **Findings**: matched on the finding kind plus the cited clause reference (`PossibleExclusion|4.1`); a duplicate has no
  clause. Micro-averaged precision and recall.
- **Route**: accuracy and a confusion matrix of expected against predicted (`StraightToHandler`, `SeniorReview`, and
  `None` for a run with no assessment). A document stopped by the input-quality gate is expected to be `SeniorReview`.
- **Citation groundedness**: of the clauses cited, the share that exist in the corpus and the share whose quoted text equals
  the wording's (whitespace aside). For the rules assessor this is **100% by construction**: it can only cite what
  `search_policy_wording` returned in the same run, and the text shown is the wording's. It is in the gate so that a
  change which breaks the citation guard is caught, and it becomes informative with the agent assessor in Milestone 5.

### Auto-routable versus needs a person

A claim is **auto-routable** when the route is `StraightToHandler` and no blocking validation issue was raised: a handler
can check and approve it. Everything else needs a person first, and that is the system working, not failing. The report
gives the rate for the pipeline and for the ground truth, the precision (of those auto-routed, how many should have been)
and the recall. It also reports "no data repair needed" (no blocking error), which is measure M3 in
[01-success-metrics.md](../docs/01-success-metrics.md), and the **silent error rate**, which is guardrail G2: of the claims
that raised no issue at all, the share with a wrong or missing critical field.

### Latency and cost

p50 and p95 per stage, in milliseconds, from the pipeline's own spans (one warm-up claim is discarded). These are
in-process local numbers: a few milliseconds a claim, with no model and no network. They say nothing about model latency,
and the gate on them is deliberately loose because CI machines vary. Cost per claim is £0.00 locally. For a model run it is the tokens each call reports times the registry price
(see below).

## Thresholds

`thresholds.json` holds a `min` or `max` per metric. Each bound is **the measured value on the test split of the
committed baseline, minus a margin of about 0.03 to 0.05**, rounded: not an aspiration. The run is deterministic apart from
latency, so the margin is room for a small, deliberate change, not noise. A threshold naming a metric the run does not
produce, or one that was not measured, fails: a typo must not switch the gate off. When the extractor improves, raise the
bounds in the same change; when a change legitimately lowers a number, lower the bound in the change and say why.

## Model comparison (Milestone 5)

**Never run against a real model.** Everything in this section is verified against scripted fakes only.

### The registry

`evals/models.json` has one entry per candidate: `id`, `kind` (`azure-openai`, `azure-ai-inference`, `foundry-anthropic`),
`endpoint`, `deployment`, `enabled`, and a `price` in GBP per 1M input and output tokens with a `sourceUrl`, an `asOf` date and
a `status` that is `verified` or `verify-before-use`. The three shipped entries are disabled placeholders and every price is
`verify-before-use`: the Azure OpenAI price page was opened on 14 November 2026 and its price cells did not render, and
Claude on Foundry is billed in Claude Consumption Units at Anthropic's token rates, so none could be confirmed. There are no
keys in the file; authentication is `DefaultAzureCredential`. A disabled model is reported as "not yet run" and is never
run. The registry is validated on load (kind, GBP, non-negative prices, dates, no placeholder in an enabled entry).

### What a model run records

Per call, from the `IChatClient` response and a metering wrapper: tokens from `ChatResponse.Usage` (a model that reports none
is flagged and its cost shown as n/a, never as zero), call latency, retries after HTTP 429 (exponential back-off with jitter,
capped, five retries, bounded parallelism), content-filter refusals, and calls that failed after retries. Per reply, from the
extractor and the citation guard: malformed replies (empty or not JSON), schema violations (JSON of the wrong shape, counted
once per reply), and citations or findings the guard removed. The **malformed-output rate** is malformed plus schema violations
over model calls. £ per claim is summed token cost over the claims in the split, and £ per 1,000 is that times 1,000. A refusal
or an exhausted retry becomes a pipeline error for that claim, which the gate counts. `results.json` carries the model, kind,
prompt versions and hashes, the price used and the counts.

### Prompt versions

The prompts are files in `prompts/<stage>/v<n>.md` (see `prompts/README.md`). `--prompt` picks the version for both
model-backed stages and `results.json` records each version with its SHA-256. A measured version is never edited.

### The compare command

`compare` runs the baseline (heuristic and rules, a real run), then each model x prompt, over one split, and writes
`<out>/<run-id>/comparison.md` and `comparison.json`, plus a folder per configuration. The recommendation is generated from the
data by the rules in ADR 0008, repeated in every report. Quality gates are in `evals/model-thresholds.json` (the baseline's
quality bounds, a malformed-output ceiling and a p95 ceiling); cost is not gated because the recommendation minimises it. The
baseline is the reference row and is never a candidate.

The committed sample, `evals/results/comparison-sample/comparison.md`, holds only the real baseline row and three "not yet run
against real models" rows. It is the output of `compare --run-id comparison-sample` with no credentials.

### The fakes and the dev flag

`--dev-fakes` unlocks `fake-oracle` (returns the ground truth, with synthetic token counts) and `fake-noisy` (the same plus
scripted faults: unparseable replies, wrong-shaped JSON, a dropped amount, a shifted date, a one-off 429, a content-filter
refusal, an invented citation). They exist to show the pipeline works: tokens are accounted, cost is computed from them, the
malformed and retry paths are counted, and the rules pick the oracle because the noisy model, though cheaper, fails the gate.
Their prices are invented. Any row they produce is labelled "FAKE — pipeline demonstration, not a model result" in the table
itself. They are not in `models.json`, and the flag is refused without `--dev-fakes`. Fake output is never a model result.

Real runs: [docs/runbook-model-comparison.md](../docs/runbook-model-comparison.md).

## Limitations

- **Synthetic data, one author.** The same person wrote the dataset, the generator, the heuristic extractor and the
  wordings. The documents are messy in the ways that author thought of, and the extractor's patterns were written by
  someone who had seen similar text. This is a known bias and it flatters the numbers. Real claims will score lower.
- **Small groups.** 36 test cases. One case is nearly 3 points of route accuracy and 7 points of the silent-error rate
  (14 unflagged claims). Per-tag rows with one or two cases are anecdotes, not rates.
- **Overlapping dev and test.** The split is by hand, and test cases share templates with dev ones. Improvements to
  patterns that dev motivated may help the test split for the wrong reason.
- **Rules assessor.** Ground truth for findings assumes the wordings say what the generator's tables say. The rules
  assessor's keyword matching is not evaluated against a handler's judgement of cover.
- **Model comparison is unproven against real services.** Response shapes, usage reporting, rate-limit behaviour and
  structured-output support are untested. Latency in a comparison is end to end per claim with several claims in flight.
- **Not measured:** real-model accuracy, cost, model latency, OCR of real scans (Document Intelligence), non-English
  claims beyond the gate, and any document longer than a page or two.
