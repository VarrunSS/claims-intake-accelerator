"""Tests for scripts/role_matrix.py. Run: python -m unittest discover -s scripts -p "test_*.py" -v

They compile infra/main.bicep once (a standalone Bicep CLI in .tools/, `bicep` or `az bicep`), or read a compiled template named in
the ROLE_MATRIX_ARM_JSON environment variable. With neither, they are skipped and say so. The mutation tests change a copy of the compiled
template the way a careless pull request would, and check that the assertion fails.
"""

from __future__ import annotations

import copy
import json
import os
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import role_matrix as rm  # noqa: E402


def _load_template() -> dict | None:
    explicit = os.environ.get("ROLE_MATRIX_ARM_JSON")
    if explicit:
        return json.loads(Path(explicit).read_text(encoding="utf-8"))
    try:
        return rm.compile_bicep(rm.ROOT / "infra" / "main.bicep")
    except RuntimeError:
        return None


TEMPLATE = _load_template()
EXPECTED = rm.load_expected(rm.ROOT / "infra" / "role-matrix.expected.json")


def _role_module(template: dict) -> dict:
    return next(r for r in rm._resources(template) if r.get("name") == rm.ROLE_MODULE)["properties"]["template"]


def _assignments(template: dict) -> list[dict]:
    return [r for r in rm._resources(_role_module(template)) if r["type"] == "Microsoft.Authorization/roleAssignments"]


def _find(template: dict, principal_parameter: str, role_key: str) -> dict:
    return next(
        a for a in _assignments(template)
        if f"parameters('{principal_parameter}')" in a["properties"]["principalId"]
        and f"roleIds').{role_key}" in a["properties"]["roleDefinitionId"]
    )


@unittest.skipIf(TEMPLATE is None, "no Bicep CLI and no ROLE_MATRIX_ARM_JSON: the role matrix was not checked")
class RoleMatrixTests(unittest.TestCase):
    def setUp(self) -> None:
        self.template = copy.deepcopy(TEMPLATE)

    def test_the_compiled_template_has_exactly_the_expected_roles(self) -> None:
        _, differences = rm.check(self.template, EXPECTED)
        self.assertEqual([], differences)

    def test_the_legacy_mock_has_no_azure_role(self) -> None:
        actual, _ = rm.matrix(self.template)
        self.assertEqual([], actual["legacy"])

    def test_the_api_cannot_send_to_service_bus_or_touch_blobs(self) -> None:
        actual, _ = rm.matrix(self.template)
        roles = {e["role"] for e in actual["api"]}
        self.assertNotIn("Azure Service Bus Data Sender", roles)
        self.assertFalse([r for r in roles if r.startswith("Storage Blob") or r.startswith("Key Vault")], roles)

    def test_every_assignment_is_scoped_to_a_resource_not_a_resource_group_or_subscription(self) -> None:
        for assignment in _assignments(self.template):
            self.assertIn("resourceId(", assignment["scope"])
            self.assertNotIn("resourceGroup()", assignment["scope"])

    def test_the_queue_roles_are_on_the_queue_not_the_namespace(self) -> None:
        actual, _ = rm.matrix(self.template)
        for identity in ("function", "api"):
            for entry in actual[identity]:
                if "Service Bus" in entry["role"]:
                    self.assertTrue(entry["scope"].startswith("Microsoft.ServiceBus/namespaces/queues:"), entry)

    def test_adding_sender_to_the_api_fails(self) -> None:
        extra = copy.deepcopy(_find(self.template, "functionPrincipalId", "serviceBusDataSender"))
        extra["properties"]["principalId"] = "[parameters('apiPrincipalId')]"
        _role_module(self.template)["resources"].append(extra)
        _, differences = rm.check(self.template, EXPECTED)
        self.assertTrue(any(d.startswith("api: UNEXPECTED Azure Service Bus Data Sender") for d in differences), differences)

    def test_widening_a_queue_role_to_the_namespace_fails(self) -> None:
        receiver = _find(self.template, "apiPrincipalId", "serviceBusDataReceiver")
        receiver["scope"] = "[resourceId('Microsoft.ServiceBus/namespaces', parameters('serviceBusName'))]"
        _, differences = rm.check(self.template, EXPECTED)
        self.assertTrue(any("MISSING Azure Service Bus Data Receiver" in d for d in differences), differences)
        self.assertTrue(any("UNEXPECTED Azure Service Bus Data Receiver" in d for d in differences), differences)

    def test_giving_the_legacy_mock_a_role_fails(self) -> None:
        extra = copy.deepcopy(_find(self.template, "apiPrincipalId", "monitoringMetricsPublisher"))
        extra["properties"]["principalId"] = "[parameters('legacyPrincipalId')]"
        _role_module(self.template)["resources"].append(extra)
        _, differences = rm.check(self.template, EXPECTED)
        self.assertTrue(any(d.startswith("legacy: UNEXPECTED") for d in differences), differences)

    def test_removing_a_needed_role_fails(self) -> None:
        sender = _find(self.template, "functionPrincipalId", "serviceBusDataSender")
        _role_module(self.template)["resources"].remove(sender)
        _, differences = rm.check(self.template, EXPECTED)
        self.assertTrue(any(d.startswith("function: MISSING Azure Service Bus Data Sender") for d in differences), differences)

    def test_a_role_assignment_outside_the_role_module_fails(self) -> None:
        stray = copy.deepcopy(_find(self.template, "apiPrincipalId", "monitoringMetricsPublisher"))
        monitoring = next(r for r in rm._resources(self.template) if r.get("name") == "monitoring")
        monitoring["properties"]["template"]["resources"].append(stray)
        _, differences = rm.check(self.template, EXPECTED)
        self.assertTrue(any("outside modules/roles.bicep" in d for d in differences), differences)

    def test_a_role_this_check_does_not_know_fails(self) -> None:
        owner = _find(self.template, "apiPrincipalId", "monitoringMetricsPublisher")
        _role_module(self.template)["variables"]["roleIds"]["monitoringMetricsPublisher"] = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635"
        _, differences = rm.check(self.template, EXPECTED)
        self.assertTrue(any("does not know" in d for d in differences), differences)
        del owner


class ExpectedFileTests(unittest.TestCase):
    """These need no compiler."""

    def test_the_expected_file_lists_every_identity(self) -> None:
        self.assertEqual(set(rm.IDENTITIES), set(EXPECTED))

    def test_the_expected_legacy_list_is_empty(self) -> None:
        self.assertEqual([], EXPECTED["legacy"])

    def test_the_compare_function_names_a_missing_and_an_unexpected_role(self) -> None:
        actual = {i: list(EXPECTED[i]) for i in rm.IDENTITIES}
        actual["api"] = actual["api"] + [{"role": "Azure Service Bus Data Sender", "scope": "x"}]
        actual["function"] = actual["function"][1:]
        differences = rm.compare(actual, EXPECTED)
        self.assertIn("api: UNEXPECTED Azure Service Bus Data Sender on x", differences)
        self.assertTrue(any(d.startswith("function: MISSING") for d in differences))


if __name__ == "__main__":
    unittest.main()
