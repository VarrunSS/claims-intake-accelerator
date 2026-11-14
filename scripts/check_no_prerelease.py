#!/usr/bin/env python3
"""Fails when a prerelease NuGet package reaches src/ (SC-03).

The lock files (packages.lock.json) next to each project under src/ list every package the project resolves, direct and transitive. A resolved
version with a hyphen (1.0.0-beta.5, 10.0.0-preview.1) is a prerelease. The only prerelease packages the repository allows are the two in
evals/ClaimsIntake.Evals, which is evaluation tooling and never ships: Azure.AI.Inference (beta) and Microsoft.Extensions.AI.AzureAIInference
(preview). They are named in Directory.Packages.props, under the "Evals" label.

    python scripts/check_no_prerelease.py          # exit 0 clean, 1 a prerelease reached src/, 2 no lock files found
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
IGNORED_PARTS = {"bin", "obj", "node_modules"}


def lock_files(folder: Path) -> list[Path]:
    return sorted(p for p in folder.rglob("packages.lock.json") if not IGNORED_PARTS & set(p.relative_to(ROOT).parts))


def prereleases(lock_file: Path) -> list[tuple[str, str]]:
    data = json.loads(lock_file.read_text(encoding="utf-8"))
    found = set()
    for framework in data.get("dependencies", {}).values():
        for name, info in framework.items():
            resolved = info.get("resolved") or ""
            if "-" in resolved:
                found.add((name, resolved))
    return sorted(found)


def main(root: Path = ROOT) -> int:
    files = lock_files(root / "src")
    if not files:
        print("No packages.lock.json found under src/. Restore the solution first.", file=sys.stderr)
        return 2

    failures = 0
    for lock_file in files:
        for name, version in prereleases(lock_file):
            failures += 1
            print(f"{lock_file.relative_to(root)}: prerelease package {name} {version}", file=sys.stderr)

    if failures:
        print("A prerelease package reached src/. Prerelease packages are allowed in evals/ only (docs/adr/0008-model-selection-and-tuning.md).", file=sys.stderr)
        return 1

    print(f"No prerelease package in {len(files)} lock files under src/.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
