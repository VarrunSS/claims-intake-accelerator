# ADR 0007: Evaluation, input quality and a deterministic notification date

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](../00-discovery.md).

- Status: accepted. Built and run locally. Measures the local heuristic extractor and rules assessor only; the
  model-backed paths have never been evaluated against a real model.
- Date: 2026-11-14

## Context

Milestones 2 and 3 produced a pipeline with 459 unit tests, and no answer to the question the customer will ask: how
often is it right? The success measures in [01-success-metrics.md](../01-success-metrics.md) (M2 field accuracy, M3 claims
needing no data repair, guardrail G2) need an evaluation, and Milestone 5 needs something to compare models against. Three
things had to be settled.

1. **How to evaluate** without AI credentials, and without fooling ourselves.
2. **What to do with documents that are not usable claims** (an unreadable scan, a marketing email) instead of failing or
   guessing.
3. **A defect from Milestone 3.** The notification-period check compared the loss date with the day the claim was
   *processed*. The samples are dated around 14 November 2026, so every "clean" sample drifted to "late" as time passed,
   and any evaluation built on it would rot.

## Decision

### 1. A .NET evaluation runner over a synthetic, tagged, split dataset

- **Runner.** `evals/ClaimsIntake.Evals`, a console application that runs the real `ClaimIntakePipeline` in process. It
  reuses the extractor, assessor, policy search and quality gate, so there is no second implementation to drift. The
  alternative, a Python harness calling the pipeline over HTTP, would add a hop, a serialisation layer and a second place
  for logic to live, in return for the Azure AI Evaluation SDK, which Milestone 5 can still use for model-graded metrics.
- **Dataset.** 51 synthetic claims at Milestone 4 (58 now: Milestone 6a added seven prompt-injection cases) with ground truth, generated deterministically (fixed seed, a random stream per case)
  from a specification. The expected values come from the specification and the wordings, never from running the
  pipeline, so a pipeline error is visible as an error. Each case is tagged with its difficulties so weaknesses show.
- **Split.** 15 `dev` cases to look at and improve against, 36 `test` cases held out at Milestone 4 (18 and 40 now, after the injection cases). The test report lists no per-case
  misses. Improvements are made against dev only.
- **Metrics.** Strict field-level precision, recall and F1 with explicit normalisation; issue and finding precision and
  recall; route accuracy and a confusion matrix; citation groundedness; the auto-routable rate and the silent error rate;
  latency per stage; per-tag breakdown. Definitions are in [evals/README.md](../../evals/README.md).
- **Gate.** `evals/thresholds.json` holds a bound per metric, set from the measured test-split baseline minus a small
  margin. The runner exits 1 below a bound. CI runs it on the test split, writes the report to the job summary, uploads it
  and fails the build. It needs no credentials, so it also runs on pull requests from forks.
- **Honest scope.** The gate measures the local baseline. It is not a claim about any model. The runner refuses
  extractor and assessor names it cannot run offline, and says so.

### 2. Input-quality checks behind `IInputQualityCheck`, routed to a person

Five checks run before extraction, behind one seam, in two stages:

| Stage | Check | Finds |
|---|---|---|
| Document (bytes) | `DocumentSizeCheck` | An empty document; one over the limit (10 MB, configurable under `Intake:Quality`). |
| Document | `PdfIntegrityCheck` | A password-protected PDF; a corrupt or truncated PDF; a file named `.pdf` that is not one. |
| Document | `DuplicateFileCheck` | The same SHA-256 received before under another document id. A redelivery under the same id is not a duplicate. |
| Text | `ReadableTextCheck` | A scan that yielded no text, or fewer than 40 letters and digits. |
| Text | `ClaimContentCheck` | Text that is not English (few common English words), marketing, or has too few claim words. |

A finding does not throw. The pipeline hands the document on as a claim with no fields, one blocking issue that says why
(`UnreadableDocument`, `NotAClaim`, `DocumentTooLarge`, `ProtectedOrCorruptDocument`, `DuplicateDocument`), the text it
could read, and a `SeniorReview` assessment from an `input-quality` assessor. A handler sees it in the queue and can reject
it; it cannot be approved, because the issue is blocking ([ADR 0002](0002-human-approval-for-writes.md)). The message never
quotes the document. The keyword checks are local and free; a model-based classifier could replace them behind the same
seam.

Consequences accepted: the gate errs towards sending a document to a person, which is the cheap mistake. The
duplicate-file record is in memory, so it forgets on restart and is not shared between instances; a Table Storage store
keyed by hash is the production version. `PdfIntegrityCheck` uses the local PDF library, so in Azure mode a valid PDF it
cannot parse is also routed to a person. Between 10 MB and the Function's 50 MB read limit a document is handed to a
person; beyond that the blob is poisoned and stays visible ([ADR 0001](0001-event-driven-pipeline.md)).

### 3. The notification date comes from the document, or from when it was received

The date a claim was notified is, in order:

1. the date the document states: a labelled field ("Date notified", "Date reported", "Notified on") or the sent date of
   an email (`Date:` or `Sent:`), read by `NotificationDateReader`, which works on text and so serves any extractor;
2. otherwise the **received timestamp** the host passes in `IntakeDocument.ReceivedAt`: the blob's creation time in the
   Function, the file's creation time in the local watcher;
3. otherwise the pipeline's `TimeProvider`, for a caller that supplies nothing.

The same date drives the future-loss-date check and the notification-period check, and is returned on the hand-off
(`NotifiedOn`, `NotifiedOnSource`). Nothing reads the wall clock at processing time, so a document gives the same answer
whenever it is processed. The evaluation fixes the clock and the received time per case from the case's metadata. The
samples now state a "Date notified", so the demo is deterministic; only `08-garbled-layout.pdf` states none and falls back
to the file's creation time.

The notification date is **not** a sixth extracted field. It is an input to the assessment, not something a handler
edits, and adding a field would change the model schema and the review UI for no gain. The evaluation scores it as a field
anyway.

## Alternatives considered

- **Use the processing time but freeze it in tests.** Keeps the defect in production, where a delayed message would be
  wrongly "late".
- **Fix the date in the sample files only.** Hides the defect. It would still be wrong for a real claim processed after a
  queue delay.
- **Evaluate with an LLM as judge now.** There is no model to call, and a judge adds a second unmeasured system.
- **Throw on an unreadable document.** It fails visibly in the Function, but locally the file is moved to `failed/` where
  nobody looks, and a marketing email is not an error. A review item with a reason is the honest outcome.

## Consequences

- The pipeline's behaviour changed in three visible ways: empty, oversized, corrupt and unreadable documents are now
  handed on for review instead of throwing (an unsupported file type still throws); the Function binds a `BlobClient` to read
  the creation time; the hand-off message carries `NotifiedOn` and `NotifiedOnSource`.
- The numbers are only as good as the dataset. The author wrote the data, the extractor and the wordings, so the numbers
  show where the heuristics are weak and flatter their strength. Real claims, a third split, are the fix.
- `evals/thresholds.json` must move with the extractor, in the same change.

## Not verified

The Azure paths are unchanged in status: never run. The `BlobClient` binding and the blob creation time are compiled and
unit-tested through `Process`, not run in a Function host. The agent assessor and the Foundry extractor are not evaluated.
