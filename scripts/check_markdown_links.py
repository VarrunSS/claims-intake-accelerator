"""Checks the relative links and heading anchors in the repository's Markdown files.

Usage: python scripts/check_markdown_links.py

Only files tracked by git are checked (falling back to a directory walk outside a repository), so node_modules and build output are
ignored. External links (http, https, mailto) are not fetched: the check runs offline. Links inside code spans and fenced code
blocks are ignored. Exit code 0 when every relative link and anchor resolves, 1 otherwise.
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parent.parent
LINK = re.compile(r"(?<!\!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)|!\[[^\]]*\]\(([^)\s]+)\)")
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")
SKIP_SCHEMES = ("http://", "https://", "mailto:", "tel:")


def markdown_files() -> list[Path]:
    try:
        out = subprocess.run(
            ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "*.md"],
            cwd=ROOT, capture_output=True, text=True, check=True,
        ).stdout
        return [ROOT / p for p in out.split("\0") if p]
    except (OSError, subprocess.CalledProcessError):
        return [p for p in ROOT.rglob("*.md") if "node_modules" not in p.parts and ".tools" not in p.parts]


def slug(heading: str) -> str:
    """GitHub's anchor for a heading: lower case, punctuation dropped, spaces to hyphens."""
    text = re.sub(r"`([^`]*)`", r"\1", heading)
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)
    text = text.strip().lower()
    text = re.sub(r"[^\w\- ]", "", text, flags=re.UNICODE)
    return text.replace(" ", "-")


def strip_code(lines: list[str]) -> list[tuple[int, str]]:
    """Lines outside fenced code blocks, with inline code spans blanked out."""
    kept, fence = [], None
    for number, line in enumerate(lines, 1):
        marker = re.match(r"^\s*(```+|~~~+)", line)
        if marker:
            fence = None if fence == marker.group(1)[0] else (fence or marker.group(1)[0])
            continue
        if fence:
            continue
        kept.append((number, re.sub(r"`[^`]*`", "", line)))
    return kept


def anchors(path: Path, cache: dict[Path, set[str]]) -> set[str]:
    if path not in cache:
        found: dict[str, int] = {}
        result = set()
        for _, line in strip_code(path.read_text(encoding="utf-8").split("\n")):
            match = HEADING.match(line)
            if match:
                base = slug(match.group(2))
                count = found.get(base, 0)
                found[base] = count + 1
                result.add(base if count == 0 else f"{base}-{count}")
        cache[path] = result
    return cache[path]


def main() -> int:
    cache: dict[Path, set[str]] = {}
    problems: list[str] = []
    files = markdown_files()
    for path in files:
        for number, line in strip_code(path.read_text(encoding="utf-8").split("\n")):
            for match in LINK.finditer(line):
                target = match.group(1) or match.group(2)
                if target.startswith(SKIP_SCHEMES):
                    continue
                name, _, fragment = target.partition("#")
                destination = path if not name else (path.parent / unquote(name)).resolve()
                where = f"{path.relative_to(ROOT)}:{number}"
                if not destination.exists():
                    problems.append(f"{where}: {target} does not exist")
                elif fragment and destination.suffix == ".md" and unquote(fragment).lower() not in anchors(destination, cache):
                    problems.append(f"{where}: {target} has no such heading")
    for problem in problems:
        print(problem)
    print(f"Checked {len(files)} Markdown files: {len(problems)} broken link(s).")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
