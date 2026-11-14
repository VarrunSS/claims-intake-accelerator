#!/usr/bin/env python3
"""Role-matrix assertion for infra/main.bicep (INF-01, INF-02).

Reads the compiled ARM template, collects every Microsoft.Authorization/roleAssignments resource (in nested deployments too), and
checks that each managed identity holds exactly the roles in infra/role-matrix.expected.json, each on the scope that file names, and that
the legacy mock holds none. Anything extra, missing or in another place fails: adding Sender to the API, or any role to the legacy mock,
or a role assignment outside modules/roles.bicep, is a red build.

    python scripts/role_matrix.py                      # compiles infra/main.bicep (needs `bicep` or `az bicep`)
    python scripts/role_matrix.py --arm infra/main.json   # checks an ARM template you compiled already

Needs Python 3.9 or later and nothing from PyPI. Exit code 0: matches. 1: differs. 2: could not run.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Built-in role definition ids (Azure built-in roles documentation) and their names. An id not listed here is a failure: a role must be
# added here, and to the expected matrix, on purpose.
ROLE_NAMES = {
    "b7e6dc6d-f1e8-4753-8033-0f276bb0955b": "Storage Blob Data Owner",
    "ba92f5b4-2d11-453d-a403-e96b0029c9fe": "Storage Blob Data Contributor",
    "974c5e8b-45b9-4653-ba55-5f855dd0fb88": "Storage Queue Data Contributor",
    "0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3": "Storage Table Data Contributor",
    "69a216fc-b8fb-44d8-bc22-1f3c2cd27a39": "Azure Service Bus Data Sender",
    "4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0": "Azure Service Bus Data Receiver",
    "3913510d-42f4-4e42-8a64-420c390055eb": "Monitoring Metrics Publisher",
    "1407120a-92aa-4202-b7e9-c0e197c71c8f": "Search Index Data Reader",
    "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd": "Cognitive Services OpenAI User",
}

# The identity each role-module parameter stands for. The legacy mock has no parameter: it must hold nothing, so a parameter that
# looks like it is reported under "legacy" and the expected list for "legacy" is empty.
PRINCIPAL_PARAMETERS = {
    "functionPrincipalId": "function",
    "apiPrincipalId": "api",
    "eventGridPrincipalId": "eventGridTopic",
}
IDENTITIES = ["function", "api", "legacy", "eventGridTopic"]

ROLE_MODULE = "roles"

RESOURCE_ID = re.compile(r"resourceId\('(?P<type>[^']+)'(?P<rest>.*)\)\]?$")
PARAMETER = re.compile(r"parameters\('(?P<name>[^']+)'\)")
ARGUMENT = re.compile(r"parameters\('(?P<parameter>[^']+)'\)|'(?P<literal>[^']*)'")
ROLE_ID_VARIABLE = re.compile(r"variables\('roleIds'\)\.(?P<key>\w+)")


def compile_bicep(bicep_file: Path) -> dict:
    """Compiles with a standalone `bicep`, or `az bicep`. Raises RuntimeError when neither is available."""
    with tempfile.TemporaryDirectory() as folder:
        out = Path(folder) / "main.json"
        candidates = []
        local = ROOT / ".tools" / ("bicep.exe" if sys.platform == "win32" else "bicep")
        if local.exists():
            candidates.append([str(local), "build", str(bicep_file), "--outfile", str(out)])
        if shutil.which("bicep"):
            candidates.append(["bicep", "build", str(bicep_file), "--outfile", str(out)])
        if shutil.which("az"):
            candidates.append(["az", "bicep", "build", "--file", str(bicep_file), "--outfile", str(out)])
        for command in candidates:
            result = subprocess.run(command, capture_output=True, text=True, check=False)
            if result.returncode == 0 and out.exists():
                return json.loads(out.read_text(encoding="utf-8"))
            sys.stderr.write(result.stdout + result.stderr)
        raise RuntimeError("No Bicep CLI found (looked for .tools/bicep, `bicep` and `az bicep`), or the build failed.")


def _resources(template: dict) -> list[dict]:
    """The resources of a template, whether the compiler wrote them as a list or as a dictionary (languageVersion 2.0)."""
    resources = template.get("resources", [])
    return list(resources.values()) if isinstance(resources, dict) else list(resources)


def role_assignments(template: dict, module_path: tuple[str, ...] = ()) -> list[tuple[tuple[str, ...], dict]]:
    """Every role assignment in the template and its nested deployments, with the chain of deployment names that holds it."""
    found: list[tuple[tuple[str, ...], dict]] = []
    for resource in _resources(template):
        kind = resource.get("type", "")
        if kind == "Microsoft.Authorization/roleAssignments":
            found.append((module_path, resource))
        elif kind == "Microsoft.Resources/deployments":
            nested = resource.get("properties", {}).get("template")
            if nested:
                found.extend(role_assignments(nested, module_path + (str(resource.get("name")),)))
    return found


def describe_scope(scope_expression: str) -> str:
    """'Microsoft.Storage/storageAccounts:hostStorageName' from a resourceId(...) expression: the type and the parameter naming it."""
    match = RESOURCE_ID.search(scope_expression.strip())
    if not match:
        raise ValueError(f"Cannot read the scope of a role assignment: {scope_expression}")
    names = [m.group("parameter") or m.group("literal") for m in ARGUMENT.finditer(match.group("rest"))]
    return f"{match.group('type')}:{'/'.join(names)}"


def matrix(template: dict) -> tuple[dict[str, list[dict]], list[str]]:
    """The role matrix of a compiled template, and the problems that stop it being read (assignments outside the role module, unknown roles)."""
    result: dict[str, list[dict]] = {identity: [] for identity in IDENTITIES}
    problems: list[str] = []
    variables_by_module: dict[tuple[str, ...], dict] = {}

    def variables(path: tuple[str, ...]) -> dict:
        if path not in variables_by_module:
            node = template
            for name in path:
                node = next(
                    r["properties"]["template"]
                    for r in _resources(node)
                    if r.get("type") == "Microsoft.Resources/deployments" and str(r.get("name")) == name
                )
            variables_by_module[path] = node.get("variables", {})
        return variables_by_module[path]

    for path, assignment in role_assignments(template):
        if path != (ROLE_MODULE,):
            problems.append(f"A role assignment outside modules/roles.bicep (in {'/'.join(path) or 'main.bicep'}). All assignments belong in the role module.")
            continue
        properties = assignment["properties"]
        principal = PARAMETER.fullmatch(properties["principalId"].strip("[]"))
        identity = PRINCIPAL_PARAMETERS.get(principal.group("name")) if principal else None
        if identity is None:
            if principal and "legacy" in principal.group("name").lower():
                identity = "legacy"
            else:
                problems.append(f"A role assignment to a principal this check does not know: {properties['principalId']}")
                continue

        key = ROLE_ID_VARIABLE.search(properties["roleDefinitionId"])
        role_id = variables(path).get("roleIds", {}).get(key.group("key")) if key else None
        if role_id is None or role_id not in ROLE_NAMES:
            problems.append(f"A role assignment with a role this check does not know: {properties['roleDefinitionId']}")
            continue

        entry = {"role": ROLE_NAMES[role_id], "scope": describe_scope(assignment["scope"])}
        if "condition" in assignment:
            entry["conditional"] = True
        result[identity].append(entry)

    for entries in result.values():
        entries.sort(key=lambda e: (e["role"], e["scope"]))
    return result, problems


def compare(actual: dict[str, list[dict]], expected: dict[str, list[dict]]) -> list[str]:
    """Differences between the compiled matrix and the expected one, as lines a person can act on."""
    differences: list[str] = []
    for identity in IDENTITIES:
        have = {(e["role"], e["scope"]) for e in actual.get(identity, [])}
        want = {(e["role"], e["scope"]) for e in expected.get(identity, [])}
        for role, scope in sorted(have - want):
            differences.append(f"{identity}: UNEXPECTED {role} on {scope}")
        for role, scope in sorted(want - have):
            differences.append(f"{identity}: MISSING {role} on {scope}")
    return differences


def render(actual: dict[str, list[dict]]) -> str:
    lines = []
    for identity in IDENTITIES:
        entries = actual.get(identity, [])
        lines.append(f"{identity}: {'(no Azure role)' if not entries else ''}")
        for entry in entries:
            flag = "  [only when its resource is deployed]" if entry.get("conditional") else ""
            lines.append(f"  - {entry['role']}  on  {entry['scope']}{flag}")
    return "\n".join(lines)


def load_expected(path: Path) -> dict[str, list[dict]]:
    data = json.loads(path.read_text(encoding="utf-8"))
    return {identity: data.get("identities", {}).get(identity, []) for identity in IDENTITIES}


def check(template: dict, expected: dict[str, list[dict]]) -> tuple[dict[str, list[dict]], list[str]]:
    actual, problems = matrix(template)
    return actual, problems + compare(actual, expected)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--arm", type=Path, help="A compiled ARM template. Default: compile infra/main.bicep.")
    parser.add_argument("--expected", type=Path, default=ROOT / "infra" / "role-matrix.expected.json")
    args = parser.parse_args(argv)

    try:
        template = json.loads(args.arm.read_text(encoding="utf-8")) if args.arm else compile_bicep(ROOT / "infra" / "main.bicep")
        expected = load_expected(args.expected)
    except (RuntimeError, OSError, json.JSONDecodeError) as error:
        print(f"Could not run the role-matrix check: {error}", file=sys.stderr)
        return 2

    actual, differences = check(template, expected)
    print(render(actual))
    if differences:
        print("\nThe role matrix differs from infra/role-matrix.expected.json:", file=sys.stderr)
        for line in differences:
            print(f"  {line}", file=sys.stderr)
        print("If the change is intended, update the expected file in the same pull request and say why.", file=sys.stderr)
        return 1

    print("\nThe role matrix matches infra/role-matrix.expected.json.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
