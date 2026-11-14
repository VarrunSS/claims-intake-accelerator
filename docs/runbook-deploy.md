# Runbook: first deployment (not yet performed)

Nothing in this repository has been deployed. This is the intended order, and it is unverified.

1. Create the two Entra app registrations from `infra/entra/*.json` (see `infra/entra/README.md`); note their client ids.
2. `azd env new <name>`; `azd env set AZURE_REVIEW_API_CLIENT_ID <id>`; `azd env set AZURE_LEGACY_CLIENT_ID <id>`.
3. `azd provision` (stage one: everything except the Event Grid topic and subscription).
4. Grant the app roles to the managed identities: `infra/entra/assign-app-roles.ps1` (Function: `Intake.ReadHistory`, `Claims.Read`,
   `Policies.Read`; API: `Claims.Write`) and assign people to `ClaimsHandler` / `SeniorHandler` through groups.
5. `azd deploy` (the Function code must exist before its `blobs_extension` key does).
6. `azd env set AZURE_DEPLOY_EVENT_SUBSCRIPTION true`, then `azd provision` again (stage two: topic, dead-letter role, subscription, alert).
7. Check: `/health/ready` on the API, drop sample 01 in `inbox`, find one trace in Application Insights, check the roles assumed in
   `infra/role-matrix.expected.json` were enough (open questions in the review: the blob trigger's role on the inbox, Flex host storage).
