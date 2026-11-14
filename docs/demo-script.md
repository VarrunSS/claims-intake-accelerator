# Demo script: 3 minutes

A script for recording the demo video. It is written to be read aloud at a normal pace, about 330 words of narration. Where the
video link goes: the README has a marked comment on its second line (`<!-- DEMO VIDEO: ... -->`). Replace it with
`**Demo video (3 minutes):** <link>` once the video is uploaded.

The video shows a local run. Say so in the first ten seconds. Do not suggest it is deployed.

## Before recording

1. Fresh state, in the repository root (PowerShell):

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-local.ps1 -Stop
   Remove-Item local-inbox -Recurse -Force -ErrorAction SilentlyContinue
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-local.ps1
   ```

   Wait for "Everything is running". Leave this terminal alone. Open a second terminal in the same folder for the commands below.
2. Browser at <http://localhost:4200>, 1280 by 800, zoom 100%, the "Sign in as" selector on **Claims handler (dev)**. Close other tabs.
3. In the second terminal, have these ready (do not run them yet):

   ```powershell
   Copy-Item samples/01-water-damage-clean.pdf, samples/10-unoccupied-late-notification.pdf, samples/06-email-fire.txt local-inbox/
   dotnet run --project evals/ClaimsIntake.Evals -- --split test --thresholds evals/thresholds.json
   ```

4. Have [evals/results/baseline/report.md](../evals/results/baseline/report.md) and the README architecture diagram open in tabs.
5. A dry run first. The evaluation takes a few seconds, and the first build in a cold terminal longer, so run it once beforehand.

## Script

| Time | Show | Say |
|---|---|---|
| 0:00 to 0:20 | README top, scroll to the diagram | "A UK insurer gets claims as messy emails and PDFs, and handlers re-key each one by hand. This is a reference implementation of the pipeline that does the reading: it extracts the claim, checks it against the policy wording, and puts it in front of a handler. A person approves every write. It is a fictional customer, and this is running locally: nothing here is deployed to Azure." |
| 0:20 to 0:40 | Second terminal: run the `Copy-Item` line. Switch to the browser, click Refresh | "I drop three documents in the inbox: a clean PDF, an informal email, and a claim for a building that was empty for 45 days. The same pipeline code runs on Azure through a Function; locally a folder watcher stands in for the trigger. Here they are in the review queue, with confidence and issue counts." |
| 0:40 to 1:15 | Open `01-water-damage-clean.pdf`. Click the Claimant field so its source text highlights. Point at the confidence badges. Click **Approve** | "The handler sees the source text beside the extracted fields. Each field has a confidence, and anything under 80% is flagged. This clean claim is covered under clause 3.2 and goes straight to a handler. Approve writes it to the legacy system once, with an idempotency key, so a retry cannot create a duplicate. Here is the legacy reference." (Point at the reference in the UI.) |
| 1:15 to 2:00 | Back to the list, open `10-unoccupied-late-notification.pdf`. Scroll to the coverage assessment. Expand clauses 4.3 and 5.1. Expand the audit trail. Point at the disabled Approve button and its hint | "This one is routed to senior review. The assessor found two problems and quotes the clauses: the loss is covered under 3.2, but the building was unoccupied over 30 days, which clause 4.3 may exclude, and it was notified late under 5.1. The clause text is the policy wording's own, never the model's, and a citation the search did not return is removed. Every tool call is in the audit trail. A claims handler cannot approve this route: that rule is in the domain, not only in the UI." |
| 2:00 to 2:10 | Second terminal: `curl.exe -i -X POST -H "Authorization: Bearer dev-handler" http://localhost:5100/claims/<id>/approve` using the id from the address bar | "The API says the same: 403 for the handler, 401 with no token. Switch the dev user to the senior handler and approval works." |
| 2:10 to 2:40 | Run the evaluation command, then show the report headline table | "The evaluation gate runs in CI and fails the build below threshold. On 40 held-out synthetic claims the offline pipeline scores field F1 0.969 and route accuracy 87.5%, and all 32 citations are real clauses. I want to be straight about what that is: no model, synthetic data written by the same author. And one guardrail is not met: 5.9% of claims that raised no flag had a wrong field, against a 1% limit." |
| 2:40 to 3:00 | README "Verified and not verified", then the engagement summary's next steps | "The infrastructure compiles and lints but has never been deployed, and no real model has been run. The next steps are a durable store, a first deployment, and a real-model comparison with the decision rules already written. The security review found 42 issues, and 35 are fixed. Everything is in the repository." |

## Exact commands and samples

| What | Command or file |
|---|---|
| Start everything | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-local.ps1` |
| Drop documents | `Copy-Item samples/01-water-damage-clean.pdf, samples/10-unoccupied-late-notification.pdf, samples/06-email-fire.txt local-inbox/` |
| List claims | `curl.exe -H "Authorization: Bearer dev-handler" "http://localhost:5100/claims?status=all"` |
| 403 for a handler | `curl.exe -i -X POST -H "Authorization: Bearer dev-handler" http://localhost:5100/claims/<id>/approve` (id of the claim from sample 10) |
| Evaluation gate | `dotnet run --project evals/ClaimsIntake.Evals -- --split test --thresholds evals/thresholds.json` |
| Stop | Ctrl+C in the first terminal, or `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-local.ps1 -Stop` |

Other samples, if there is time: `05-loss-outside-policy-period.pdf` (a blocking error, so Approve is refused), `09-flood-office-exclusion.pdf`
(a flood exclusion), `03-missing-policy-number.pdf`. [samples/](../samples/) lists what each one is meant to show.

## Things not to say

- Not "deployed", "in production" or "tested with a real model". The infrastructure compiles and lints, and nothing has run on Azure.
- Not "99%" or any accuracy figure without "offline, synthetic, 40 held-out claims".
- Not that the 100% citation figure proves a model cites correctly. For the rules assessor it is true by construction.
- Not "one-command deployment". The first deployment needs two Entra app registrations and two provisioning passes ([runbook](runbook-deploy.md)).
