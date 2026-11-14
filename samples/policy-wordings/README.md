# Policy wordings

Synthetic policy wordings for the fictional Thames Mutual Insurance. They are written for this portfolio project,
are not real insurance terms, and must not be relied on. They are the corpus that `search_policy_wording` searches
(see [ADR 0006](../../docs/adr/0006-policy-wording-rag.md)).

## Format

Each file is one wording. A short front-matter block gives the id, title and edition. Then:

- `## 4. Exclusions` is a section: a number and a name.
- `### 4.1 Flood` is a clause: a reference, a heading, and the text under it up to the next heading.

The clause is the unit of search and of citation. Nothing smaller is indexed and nothing is cited without its
reference, so a handler can open the wording at the clause the assessment names.

## Which policy uses which wording

The legacy claims system holds the link as `wordingId` on each seeded policy
(`src/ClaimsIntake.Legacy/Policies.cs`). A test checks that every seeded policy points at a wording in this folder.

| Wording | What it is | Notification period | Flood | Policies |
|---|---|---|---|---|
| `TM-PW-CC-2026` | Commercial Combined | 30 days | Excluded (4.1) | TM-CP-100001, 100002, 100003, 100008 and 100005 |
| `TM-PW-CCF-2026` | Commercial Combined with flood extension | 30 days | Covered (3.5), higher excess | TM-CP-100007, 100010 |
| `TM-PW-OF-2026` | Office Package | 14 days | Excluded (4.1) | TM-CP-100006, 100009 |
| `TM-PW-EN-2026` | Engineering and Plant | 21 days | Excluded (4.2) | TM-CP-100004 |

TM-CP-100005 is a Motor Fleet policy. Its motor cover is out of scope here, so the seed data points its premises
and depot section at the Commercial Combined wording.

All four wordings exclude wear and tear and buildings left unoccupied for more than 30 consecutive days, so the
exclusion checks have something to find.
