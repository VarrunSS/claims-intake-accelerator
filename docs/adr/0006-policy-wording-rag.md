# ADR 0006: Policy wording search, agent tools and the citation guard

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](../00-discovery.md).

- Status: accepted. The local path is built and run. The Azure path (Azure AI Search, the agent on a real model,
  Table Storage) is built and unit-tested against fakes, and has never run against the real services.
- Date: 2026-11-14

## Context

A handler deciding whether a claim is covered needs the clauses of the policy wording that matter: the one that
gives cover, any exclusion that may apply, the notification period, the excess. Finding them by hand in a
20-page wording is slow, and a model asked about cover from memory will invent clauses. The cost of an invented
clause is high: a handler who trusts "excluded under clause 4.7" and declines a claim that clause 4.7 does not
exist for has made a wrong decision on a real claim.

Constraints that shape the design:

- A person decides every claim ([ADR 0002](0002-human-approval-for-writes.md)). The assessment is advice.
- Documents are untrusted, so the agent's tools must be safe to call whatever the document says.
- Everything has to run offline for development, CI and the reader of this repository
  ([ADR 0005](0005-local-development-mode.md)): no Azure account, no model keys, no Docker.
- Telemetry and audit records may hold ids and counts, never claim text, names or free text.

## Decision

### 1. Chunk by clause

Each policy wording is a markdown file with numbered sections (`## 4. Exclusions`) and clauses
(`### 4.1 Flood`). The **clause is the chunk**: one reference, one heading, one body. Nothing is split below a
clause and nothing is merged across clauses.

Why: a clause is the unit a handler cites and reads, and the unit an insurer's wording is amended in. A
fixed-size window (say 500 tokens with overlap) would cut a clause in two and return half an exclusion, and the
cited "clause" would have no number. Clause chunks are small (a few sentences), so the whole clause is returned
and shown without trimming. `PolicyWordingParser` does the chunking and refuses a wording with a duplicate
clause number, an empty clause or no front matter.

### 2. One interface, two implementations

`IPolicyWordingSearch.SearchAsync(query, wordingId?, top)` returns ranked clauses, each with wording id, clause
reference, section, heading, text and score.

| | Local (default) | Azure |
|---|---|---|
| Class | `Bm25PolicyWordingSearch` | `AzureAiSearchPolicyWordingSearch` |
| Retrieval | BM25, written in full (about 60 lines) | Hybrid query: keyword (BM25) and vector in one request, fused by the service |
| Embeddings | None | `IEmbeddingGenerator<string, Embedding<float>>` (`Microsoft.Extensions.AI`), an Azure OpenAI embedding deployment |
| Credentials | None | `DefaultAzureCredential`; the index is read with the Search Index Data Reader role |
| Chosen by | `Intake:PolicySearch` = `Local` | `Intake:PolicySearch` = `AzureAiSearch` |

**Local BM25.** Terms are lower-cased, stop words are dropped and a light suffix stripper joins flood, floods,
flooded and flooding. Each clause is one document, with the heading counted twice so that a clause called
"Flood" outranks one that merely mentions flooding. Corpus-wide statistics are used even when a search is limited
to one wording, so a clause's score does not change with the filter. Ties break on wording id and then clause
number, so the same query always gives the same ranking. This is not a toy: for a corpus of a few hundred
clauses with distinctive headings, keyword search ranks well, it is explainable ("these words matched"), and it
makes the whole assessment deterministic and testable without a model.

**Azure hybrid.** Keyword search alone misses a question phrased in words the wording does not use ("rain got into
the basement" against a clause about "surface water"). Vector search alone is poor at exact terms and clause
numbers. A hybrid query does both and the service fuses the rankings. The index definition
(`PolicyWordingIndex.Definition`) is in code: `wordingId` filterable, the `en.microsoft` analyser on the text
fields, and a 1536-dimension HNSW vector field. `WordingIngestor`, run by the `ClaimsIntake.Ingestion` console tool,
chunks, embeds and uploads, keyed by wording and clause so running it again updates and does not duplicate.
`--dry-run` chunks and prints without any network.

A wording id is checked against a strict pattern before it reaches a filter expression, because the id can come
from a model.

### 3. Four tools, plain C#

The tools are public methods on `ClaimTools`, tested directly, and offered to the model by wrapping them with
`AIFunctionFactory.Create`.

| Tool | Does | Writes? |
|---|---|---|
| `lookup_policy` | Reads a policy from the legacy system: period, excess, wording id | No |
| `search_policy_wording` | Calls `IPolicyWordingSearch` | No |
| `check_duplicate` | Same policy, same loss date, amount within 10 per cent, in the review store or the legacy system | No |
| `propose_claim` | Records the proposed route in memory and says "proposed for human review" | **No** |

`propose_claim` is the renamed `create_claim` of ADR 0002 and keeps its promise: **it never writes to the legacy
system.** This is enforced by construction, not by the prompt: `ClaimTools` is given a policy lookup, a wording
search, a read-only claim lookup and a clock, and no writer, so there is nothing for it to call. A test checks the
constructor. The legacy write still happens only in the API after a named handler approves.

Arguments from a model are untrusted. A policy number that does not match `[A-Za-z0-9-]{1,40}` is not sent to the
legacy system (the lookup answers "not found"), a query is capped at 500 characters, the result count is clamped,
and a bad route is refused.

### 4. Two assessors behind `IClaimAssessor`

An assessor produces coverage findings: the clauses relied on, possible exclusions, a notification breach, a
possible duplicate, and a recommended route (straight to a handler, or senior review). It runs in
`ClaimIntakePipeline` after validation and before the hand-off, so the findings travel with the claim.

- **`RuleBasedClaimAssessor`** (default, local). Calls the same tools in a fixed order: look up the policy, search
  its wording with the cause of loss and read the insured-peril clause that comes back, search for each exclusion
  the description points to (flood, wear and tear, unoccupied for more than 30 days) and confirm it from the clause
  returned, read the notification period **from the wording's notification clause** and compare it with the dates,
  check for a duplicate. It recognises a handful of causes and exclusions by keyword. It is a baseline, and says
  so in its notes when it recognises nothing.
- **`AgentClaimAssessor`** (Azure). Microsoft Agent Framework 1.28.0 (`Microsoft.Agents.AI`, pinned exactly, see
  [ADR 0004](0004-dotnet-agent-framework.md)): a `ChatClientAgent` over `IChatClient`, the four tools registered as
  `AIFunction`s, the claim passed as a JSON object between `<claim>` tags (a closing tag cannot occur inside it), and a
  system prompt that requires a citation for every finding. The tool loop is capped at eight iterations and at **24 tool
  calls** in all (Milestone 6a).

Either is chosen by `Intake:Assessor` (`Rules`, `Agent` or `None`). If an assessor throws, the claim is **not**
lost: the hand-off carries a senior-review assessment that says the automatic assessment failed, with the tool
calls made so far.

### 5. The citation guard

A prompt asking for citations is a request, not a control. After the agent answers, `CitationGuard` checks the
answer against what the tools actually returned **in this run**:

- A citation (wording id and clause reference) that `search_policy_wording` did not return is removed, and a note
  says so.
- **A citation must belong to this claim's wording** (Milestone 6a, AI-01). The pipeline looks the policy up itself and
  binds its wording id and policy number into `ClaimTools` in code. `search_policy_wording` has no wording parameter any
  more, so the model cannot search another wording, and `CitationGuard.Validate` also removes any citation whose wording id
  is not the expected one. `lookup_policy`, `check_duplicate` and `propose_claim` refuse any other policy number, and the
  refusal sends the claim to senior review. `lookup_policy` no longer returns the insured's name.
- A finding that needs a citation and has none left is dropped, with a note.
- A possible-duplicate finding must name a claim that `check_duplicate` returned.
- A citation that passes is **replaced by the clause as the search returned it**. The text a handler reads is the
  wording's, never the model's.
- Any removed citation, dropped finding or unreadable answer sends the claim to senior review. The model cannot
  lower the route when a finding is an exclusion, a late notification or a duplicate.
- Anything that is not valid JSON is discarded with a note. Model text is stripped of control characters and
  length-capped.

**The route floor** (Milestone 6a, AI-02). The guard can only remove things, but omission is the attack that matters: a
document that talks the model out of reporting an exclusion. So with the agent assessor the rules assessor also runs, with
its own bound tools, and the final route is the more cautious of the two. Findings the rules check made and the agent did not
are added, a note says the two disagreed, and `claims.assessment.route_floor_raised` counts it. The agent can raise the
route, never lower it. **A tool-call budget** (24) makes a model that loops fail the assessment, which becomes a
senior-review assessment (the framework swallows a tool's exception, so the assessor checks a flag).
**Untrusted text is JSON-encoded** in both prompts with angle brackets escaped (AI-03); an `IPromptAttackDetector` seam
(no-op locally) is where Azure AI Content Safety Prompt Shields would sit, unproven.

The guard is tested with a scripted `IChatClient` that emits tool calls and then an answer, including an answer
that cites a clause that does not exist, a model that cites without searching, a duplicate that was never found,
and a model that tries to say "straight to handler" over an exclusion.

What the guard does not do: it proves the cited clause exists and was retrieved, not that it means what the
model says it means. A real model can cite a real clause for the wrong reason. That is for the handler, who sees
the clause text next to the finding, and for the evaluation set in Milestone 4, which will score citation
groundedness against labelled claims.

### 6. Audit log

Every tool call is recorded as an `AuditEntry`: claim id, tool, arguments, a short result summary, duration and
start time. **Arguments hold ids, dates, numbers and the length of any free text, never text.** A search query is
recorded as `[text:43 chars]`; the model's rationale likewise; a value that is not shaped like a policy number is
recorded as `[invalid:N chars]`. A result is recorded as counts and ids ("3 clauses: TM-PW-CC-2026/4.1, ...").
Tests assert that claimant names and description text never appear in an entry. Human decisions (approved, rejected,
submitted, submit_failed) are recorded in the same trail with the actor's object id, role and route
([ADR 0009](0009-identity-roles-and-segregation-of-duties.md)); rows have deterministic keys and are upserted.

The pipeline and the API are separate processes, so the trail **travels in the hand-off message** (`ProposedClaim`)
and the **API stores it** when it accepts the claim, in an `IAuditLog`: in memory locally, Azure Table Storage when
`Audit:Table:Endpoint` is set (`TableStorageAuditLog`; one partition per claim, row keys that sort by time;
`DefaultAzureCredential`; the identity's existing Storage Table Data Contributor role). `GET /claims/{id}/audit`
returns it. A redelivered message does not store the trail twice, because the claim already exists.

The trail is part of the message, so on Azure it passes through Service Bus. That is acceptable because it holds
no names, addresses or free text (it is pseudonymised, not free of personal data: policy numbers, loss dates, amounts and
the actor's object id are pseudonymous personal data under UK GDPR, and the audit store is classified accordingly), and it keeps one writer to the audit store. The alternative, the Function
writing to the table directly, is a one-line change to `ClaimTools` if the customer wants the audit written before
the hand-off.

### 7. Telemetry

Every tool call has an `Activity` span named `tool.<name>` on the existing `ClaimsIntake.Pipeline` source, with
tags `tool`, `claimId`, `duration_ms` and `succeeded`, and the exception type on failure. The assessment is one
`intake.assess` span. No document text, query text or personal data goes into a span or a log line; a test
captures the logs of a failing assessor whose exception message contains claim text and checks that none of it is
logged.

## Consequences

Good:

- The whole path runs and is tested offline: wordings, chunking, BM25, four tools, the rule-based assessor, the
  audit trail and the API and UI that show them.
- The agent cannot write, and cannot present a clause that was not retrieved.
- Swapping the model, the search service or the audit store is a configuration change behind an interface.
- Findings carry the clause text, so a handler checks the advice against the wording in one click.

Costs and risks:

- **The rule-based assessor is crude.** It matches keywords in the loss description. "Water came in through the
  roof after the storm" is a storm, not a flood, and the flood rule would flag it if it said "flood". It will miss
  causes that its patterns do not cover. It exists to make the pipeline demonstrable and testable, and as the
  baseline the agent is compared against in Milestone 4.
- **The synthetic wordings are small and tidy.** Four wordings, 84 clauses, written to be searchable. Real
  wordings have endorsements, schedules, cross-references and clauses that qualify each other. Retrieval quality
  on them is unmeasured.
- **The Azure path has never run.** `AzureAiSearchPolicyWordingSearch`'s SDK adapter (`SearchClientClauseQuery`),
  the index creation and upload (`SearchIndexClauseWriter`), the table adapter (`TableClientAuditTable`), the
  agent against a real model and the Azure OpenAI embedding client are compiled and tested against fakes only.
  Option building, the index definition, keys, mapping and ordering are tested; the calls themselves are not.
  Expect fixes on first contact. In particular, the free Azure AI Search tier is assumed to accept Entra ID only
  access and a vector field; that is not confirmed.
- **Agent Framework moves fast** (28 minor releases in seven months, ADR 0004). The version is pinned, and the
  scripted-client tests are the early warning when an upgrade changes how tools are invoked.
- **The duplicate check reads two stores over HTTP**, the legacy system and the API's `GET /claims/history`. The
  history endpoint now needs an app-only token with the `Intake.ReadHistory` role (Milestone 6a).
- **Dates matter.** The notification check compares the loss date with the date the claim reached us. The sample
  documents are dated around 14 November 2026, so running them much later makes the "clean" ones late.

## Alternatives considered

- **Fixed-size chunks with overlap.** Standard for prose. Rejected: it cuts clauses, loses the clause number, and
  a handler cannot open "chunk 37".
- **Vector search only.** Good for paraphrase, weak for exact terms and numbers. Rejected as the sole method. It
  is half of the hybrid query.
- **Semantic ranker on Azure AI Search.** Improves ranking further, but is not available on the free tier and adds
  cost. Left off (`semanticSearch: 'disabled'`). Worth measuring in Milestone 5.
- **Asking the model to quote the clause.** Rejected: a model can quote a clause that does not exist, and the
  point of the guard is that the text shown is the retrieved text.
- **Foundry Agent Service file search.** Hosted retrieval and citations with less code. It moves retrieval and the
  tool loop into the platform, where the citation guard and the approval boundary are harder to enforce, and
  UK South availability is unconfirmed ([ADR 0004](0004-dotnet-agent-framework.md)).
- **A rules engine only, with no model.** Would be right if the wordings were fixed and the descriptions
  structured. Real claim descriptions are free text, which is what the agent is for. The rule-based assessor stays
  as the offline baseline and the fallback.
