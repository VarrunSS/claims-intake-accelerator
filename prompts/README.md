# Prompts

Versioned prompts for the two model-backed stages. The file is the prompt: its text, with line endings normalised to
LF and trailing whitespace trimmed, is what is sent to the model, and its SHA-256 is recorded in `results.json` with the
version (`--prompt v1`). Change a prompt by adding a new version; never edit a version that has been measured.

| Prompt | Version | Status |
|---|---|---|
| `extraction/v1.md`, `assessment/v1.md` | v1 | The prompts that shipped in Milestones 2 and 3, moved here unchanged. |
| `extraction/v2.md`, `assessment/v2.md` | v2 | A hypothesis. **Not measured against any model yet, so not claimed to be better.** |

## The v2 hypothesis

Milestone 4 measured the heuristic extractor and found four weaknesses. They are the ones a model prompt can be told
about, so v2 changes only what is aimed at them. The hypothesis is that, for a given model, v2 raises field F1 and route
accuracy on the cases tagged `email`, `uk_dates`, `policy_typo` and the `burgled`-style cause, and costs a few hundred more
input tokens per call. It could equally do nothing, or hurt: a longer prompt can make a small model less reliable.

| M4 weakness | v1 | v2 change |
|---|---|---|
| Informal emails: the insured is named in the body, not the sender | "value exactly as written" only | Says where the claimant is usually named in an email, and what to ignore (sender, quoted mail, the insurer) |
| Two-digit years (`03/11/26`) | "dates stay as written", so the pipeline gets an ambiguous string | UK day-first, two-digit year means 20yy, output `yyyy-MM-dd`, verbatim text in the quote so the span is still found |
| Causes in the claimant's words ("burgled") | No guidance | Keep the document's own words in `loss_description`; the assessment prompt tells the model to search the wording in the wording's terms (burgled means theft) |
| Policy numbers with a letter for a digit (`TM-CP-1OO003`) | No guidance | Copy character for character and never repair, because the dataset (and the handler) want the typo reported |

The assessment v2 additionally makes the three searches (peril, exclusions, notification) mandatory, and requires a
`coverage_clause` finding before `straight_to_handler`. That targets the route misses where the model skipped the
exclusion search.

How it will be judged: `compare --models <id> --prompts v1,v2` runs both over the same split, and the comparison table
shows the difference. v2 is adopted for a model only if it improves the gate metrics for that model on the **dev** split
first and then holds on the held-out test split. Tune on dev; the test split is for the final number.
