# Contributing

This is a portfolio project, but it is set up so that a change can be checked the way CI checks it. Before opening a pull request, run:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -warnaserror
dotnet test --no-build
dotnet run --project evals/ClaimsIntake.Evals -- --split test --thresholds evals/thresholds.json
cd ui; npm ci; npm test; npm run build; cd ..
python scripts/check_markdown_links.py
python -m unittest discover -s scripts -p "test_*.py"     # 11 of the 14 need a Bicep CLI on the path or in .tools/
```

Conventions:

- British English, plain and direct. No document text, names or field values in logs, spans or the audit trail.
- A change to a package updates the lock files (`dotnet restore --force-evaluate`), and CI restores with `--locked-mode`.
- Do not edit `evals/thresholds.json` to make a failing gate pass. If a threshold moves, the commit says why, and the baseline in `evals/results/baseline/` is regenerated.
- Do not edit a prompt version that has been measured: add a new one ([prompts/README.md](prompts/README.md)).
- A design change that is hard to reverse gets an ADR in `docs/adr/`.
- Anything that cannot be run here (Azure, a real model, a real tenant) is described as not verified, in the README and in [docs/verification-status.md](docs/verification-status.md).

Report security issues as described in [SECURITY.md](SECURITY.md).
