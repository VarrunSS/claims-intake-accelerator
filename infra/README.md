# Infrastructure

Bicep for Azure (`azd up`). **Compiled and linted in CI and locally; never deployed.** See the README "What was not verified".

| File | Purpose |
|---|---|
| `main.bicep` | Subscription-scope entry point: three identities, storage x3, Service Bus, Search, Key Vault, Foundry (optional), Function, web apps, roles, Event Grid (stage two), alerts, workbook |
| `modules/roles.bicep` | **Every** role assignment, resource-scoped |
| `role-matrix.expected.json` | The allowed role matrix, asserted by `scripts/role_matrix.py` |
| `main.ci.bicepparam` | Parameters for CI (build-params, PSRule) |
| `modules/alerts.bicep`, `workbooks/` | Alert rules and the Application Insights workbook |
| `entra/` | App registration manifests and the app-role assignment script (never run) |

## Role matrix

| Identity | Role | Scope |
|---|---|---|
| Function | Storage Blob Data Owner, Queue Data Contributor, Table Data Contributor | host storage account |
| Function | Storage Blob Data Owner | `inbox` container (blob trigger; to confirm on first deployment) |
| Function | Service Bus Data Sender | queue `claims-for-review` |
| Function | Search Index Data Reader | the search service |
| Function | Cognitive Services OpenAI User | Foundry account (only when deployed) |
| Function | Monitoring Metrics Publisher | Application Insights |
| API | Service Bus Data Receiver | queue `claims-for-review` |
| API | Storage Table Data Contributor | audit storage account (its own) |
| API | Monitoring Metrics Publisher | Application Insights |
| Legacy mock | none | |
| Event Grid topic | Storage Blob Data Contributor | container `eventgrid-deadletter` (stage two) |

Check locally: `python scripts/role_matrix.py` (needs a Bicep CLI) and `python -m unittest discover -s scripts -p "test_*.py"`.

## Checks and their settings

- `bicepconfig.json`: linter rules are errors. Three are off on purpose: `what-if-short-circuiting` (principal ids and
  module outputs are runtime values by nature), and `use-recent-api-versions` / `use-recent-module-versions` (the suggested
  versions could not be deployed to check; versions are bumped by hand when a deployment is possible).
- `ps-rule.yaml`: PSRule for Azure, default baseline, with suppressions for public network endpoints (ADR 0011), cost-driven
  choices (one API instance, LRS, free Search) and diagnostics settings still to add. **Never run**; the CI step reports but does
  not block until it has run green once.
