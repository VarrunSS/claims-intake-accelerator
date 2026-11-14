# GitHub setup

What to do to publish this repository. **Nothing here has been run.** The repository has no remote and has not been pushed.

## 1. Decide about the commit author before the first push

Every commit carries an author name and email, and a public push makes them public. Check what they are:

```powershell
git log --format="%h %an <%ae>" | Sort-Object -Unique
git config user.email
```

At the time of writing all seven commits before Milestone 7 use a personal Gmail address. If you do not want that address public, do this first
(none of it is done for you, and rewriting history changes every commit hash, so do it before anyone has cloned):

1. GitHub, Settings, Emails: tick **Keep my email addresses private** and **Block command line pushes that expose my email**. Copy the
   `<id>+<username>@users.noreply.github.com` address.
2. `git config user.email "<that address>"` (this repository only).
3. Rewrite the existing commits, for example `git rebase -r --root --exec "git commit --amend --no-edit --reset-author"`, then check
   `git log --format="%h %an <%ae>"` again. (With the block setting on, a push that still carries the old address is rejected, which is a
   useful safety net.)

The `LICENSE` names "Varrun" and the `CODEOWNERS` file names `@varrunss`. Both are deliberate and public.

## 2. Repository description and topics

Description (a suggestion, under 350 characters):

> Reference implementation of a claims intake pipeline for a fictional UK insurer: documents to pre-filled claims with cited policy clauses, human approval for every write. .NET 10, Angular, Azure (Bicep), evaluation gate in CI. Runs offline; not deployed to Azure.

Topics: `azure`, `dotnet`, `csharp`, `angular`, `bicep`, `rag`, `llm-evaluation`, `ai-agents`, `microsoft-agent-framework`, `opentelemetry`, `entra-id`, `document-intelligence`, `insurance`, `reference-implementation`, `threat-model`.

## 3. Create and push

Check first: `git status` is clean, and `python scripts/check_markdown_links.py` and `dotnet test` pass. Then:

```powershell
gh auth status
gh repo create varrunss/claims-intake-accelerator --public --source . --remote origin --push `
  --description "Reference implementation of a claims intake pipeline for a fictional UK insurer: documents to pre-filled claims with cited policy clauses, human approval for every write. .NET 10, Angular, Azure (Bicep), evaluation gate in CI. Runs offline; not deployed to Azure."
gh repo edit varrunss/claims-intake-accelerator `
  --add-topic azure --add-topic dotnet --add-topic csharp --add-topic angular --add-topic bicep --add-topic rag `
  --add-topic llm-evaluation --add-topic ai-agents --add-topic microsoft-agent-framework --add-topic opentelemetry `
  --add-topic entra-id --add-topic document-intelligence --add-topic insurance --add-topic reference-implementation --add-topic threat-model
```

Optional, safer order: create it with `--private` first and look at the first CI run before making it public (`gh repo edit --visibility public
--accept-visibility-change-consequences`). Code scanning and dependency review need a public repository or GitHub Advanced Security, so on a private
one those two jobs will not give a result.

## 4. Settings to turn on

Right after the push (Settings, or the commands):

| Setting | Why | How |
|---|---|---|
| Secret scanning and push protection | The code cannot show whether they are on. Push protection stops a leaked key reaching the repository | `gh api -X PATCH repos/varrunss/claims-intake-accelerator -f "security_and_analysis[secret_scanning][status]=enabled" -f "security_and_analysis[secret_scanning_push_protection][status]=enabled"` |
| Private vulnerability reporting | [SECURITY.md](../SECURITY.md) tells people to use it | `gh api -X PUT repos/varrunss/claims-intake-accelerator/private-vulnerability-reporting` |
| Dependabot alerts and security updates | `dependabot.yml` covers version updates; these cover advisories | `gh api -X PUT repos/varrunss/claims-intake-accelerator/vulnerability-alerts` and `gh api -X PUT repos/varrunss/claims-intake-accelerator/automated-security-fixes` |
| Dependency graph | The dependency-review job needs it. On by default for public repositories | Settings, Advanced Security |
| Actions: default token read-only, approval for fork pull requests | The workflows already declare `permissions: {}` and per-job permissions | Settings, Actions, General |
| Do not enable CodeQL "default setup" | `.github/workflows/codeql.yml` is the CodeQL configuration. Two setups conflict | Settings, Advanced Security, Code scanning |

**Branch protection on `main`.** Required checks only appear in the list after they have run once, so turn this on after the first CI run (section 5),
and do not require `iac` until it is green (its PSRule step is `continue-on-error` and has never run). With one maintainer, require a pull request but zero
approvals, or you cannot merge your own:

```powershell
@'
{
  "required_status_checks": { "strict": true, "contexts": ["build-test", "ui", "evals", "Analyze (csharp)", "Analyze (javascript-typescript)"] },
  "enforce_admins": false,
  "required_pull_request_reviews": { "required_approving_review_count": 0, "dismiss_stale_reviews": true },
  "restrictions": null,
  "required_linear_history": true,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_conversation_resolution": true
}
'@ | gh api -X PUT repos/varrunss/claims-intake-accelerator/branches/main/protection --input -
```

Add `iac`, `sbom` and `dependency-review` to `contexts` once they have passed.

## 5. The first CI run

The first push is the first time `.github/workflows/` runs anywhere. The workflows were checked with actionlint 1.7.12 only, and the pinned action SHAs were
checked against their release tags with `git ls-remote`. Open the Actions tab and expect to spend an hour fixing things. In rough order of likelihood:

| Job | What to watch for |
|---|---|
| `build-test` | Everything was built and tested on Windows only. Linux can differ in path case and separators, line endings, time zones, and `dotnet restore --locked-mode`, if a lock file was generated on Windows with a different set of packages. If the restore fails, regenerate the locks with `dotnet restore --force-evaluate` and commit them |
| `ui` | `npm ci` and `npm audit` on the runner's Node version. A new advisory can fail `npm audit` on a day when nothing changed |
| `evals` | The gate has a latency bound (p95 under 250 ms per claim). It was 2.6 ms here, so a failure means something else changed. The job writes the report to the run summary |
| `iac` | **First run of `az bicep`.** The runner's Bicep version may be newer than the 0.49.2 used here, and `bicepconfig.json` makes linter rules errors, so new rules can fail the build. The role matrix runs on the compiled template. **PSRule for Azure has never run**: expect findings, some of them from suppressions in `ps-rule.yaml` that were written from reading the rules. The step is non-blocking until it passes once |
| `sbom` | First run of `anchore/sbom-action`. It should upload a CycloneDX artifact |
| `dependency-review` | Runs on pull requests only, so open a trivial pull request to see it. It needs the dependency graph and fails on new high-severity vulnerabilities |
| CodeQL | First run of both languages. The C# job builds the solution in Release first. Alerts from the `security-and-quality` suite will appear in the Security tab, and on a public repository anyone can read them, so look at them early. A weekly scheduled run follows |
| Dependabot | Starts opening weekly pull requests on Monday. It is told to ignore the two pinned prerelease packages used only by `evals/` |

After the first run, update the README's [Verified and not verified](../README.md#verified-and-not-verified) section to say which jobs have now run
and what they showed.

## 6. After publishing

- Put the demo video link in the README (see [demo-script.md](demo-script.md)).
- Pin the repository to your GitHub profile.
- If a CI fix changes a number, regenerate `evals/results/baseline/` and the figures that quote it (README, [success metrics](01-success-metrics.md), [engagement summary](engagement-summary.md), [resume bullets](resume-bullets.md)).
