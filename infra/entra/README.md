# Entra ID app registrations

Two app registrations back the authentication in [ADR 0009](../../docs/adr/0009-identity-roles-and-segregation-of-duties.md):

| File | App | App roles |
|---|---|---|
| `review-api.app-registration.json` | The review API (audience for the review UI and the intake pipeline) | `ClaimsHandler`, `SeniorHandler` (users); `Intake.ReadHistory` (apps) |
| `legacy.app-registration.json` | The legacy claims mock | `Claims.Write`, `Claims.Read`, `Policies.Read` (apps) |

**These files have never been applied.** There is no tenant in this project's environment. They are the manifest to
apply by hand or from a pipeline, with the role ids fixed so that applying them twice does not create duplicate roles.
Nothing here is a secret.

## Applying them (not run)

```bash
# 1. Create the registrations (needs Application.ReadWrite.All, or the Application Developer role).
az ad app create --display-name claims-intake-review-api --sign-in-audience AzureADMyOrg \
  --identifier-uris api://claims-intake-review-api --app-roles @<(jq .appRoles infra/entra/review-api.app-registration.json)
az ad app create --display-name claims-intake-legacy --sign-in-audience AzureADMyOrg \
  --identifier-uris api://claims-intake-legacy --app-roles @<(jq .appRoles infra/entra/legacy.app-registration.json)

# 2. Create service principals, then assign the app roles to the managed identities (Microsoft Graph appRoleAssignedTo):
#    - the Function's identity:  Intake.ReadHistory on the review API; Claims.Read and Policies.Read on the legacy app
#    - the review API's identity: Claims.Write on the legacy app
#    - people: ClaimsHandler or SeniorHandler on the review API, through groups, in Enterprise applications > Users and groups
#    The "Assignment required" setting on both service principals should be on, so that nobody without a role can get a token.
```

## Settings the services read

| Setting | Where | Value |
|---|---|---|
| `AzureAd__TenantId`, `AzureAd__ClientId` | API, legacy | The tenant and the app's client id. The service **does not start** without them. |
| `AzureAd__Audience` | API, legacy | The app's identifier URI, for example `api://claims-intake-review-api`. The client id and `api://<client id>` are always accepted too. |
| `Legacy__Scope` | API | `api://claims-intake-legacy/.default`: the API gets an app-only token for the legacy system with its managed identity. |
| `Intake__Legacy__Scope`, `Intake__Api__Scope` | Function | `api://claims-intake-legacy/.default` and `api://claims-intake-review-api/.default`. |
| `config.json` `auth.*` | UI | `clientId` of a SPA registration, `tenantId`, and `apiScope` `api://claims-intake-review-api/access_as_user`. |

`Auth__Mode` must be absent or `Entra` on Azure. `Dev` is refused outside the Development environment.
