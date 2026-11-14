# ADR 0009: Identity, roles and segregation of duties

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](../00-discovery.md).

- Status: accepted. Built and tested offline with test-signed tokens. **Never run against a real Entra ID tenant.**
- Date: 2026-11-14
- Review findings closed: SEC-01, SEC-02, SEC-03, SEC-04, SEC-05, SEC-06, SEC-07
  ([review](../reviews/2026-11-architecture-security-review.md))

## Context

Until Milestone 6 the review API had no authentication, the reviewer was free text in the request body, and any
caller could approve any claim, including one the assessor had routed to senior review. Approvals and rejections
were not audited, although guardrail G1 ("every write to the legacy system references an approval record") assumes
they are. The legacy mock accepted claim writes from anyone. ADR 0002 promised "an identified reviewer" and
"a named handler", and the code did not deliver either.

## Decision

### 1. Microsoft Entra ID, JWT bearer, everything closed by default

The review API and the legacy mock validate Entra ID access tokens with `Microsoft.Identity.Web`
(`AddMicrosoftIdentityWebApi`). The **fallback authorisation policy requires an authenticated caller**, so a new
endpoint is closed unless someone opens it on purpose. Only the health probes are `AllowAnonymous`. The service does
not start without `AzureAd:TenantId` and `AzureAd:ClientId`: there is no "auth off" default. Audience, issuer and
lifetime are validated by the library.

### 2. Two kinds of caller, two kinds of role

| Caller | Token | Roles | Where |
|---|---|---|---|
| A person (the review UI) | Delegated, user token | `ClaimsHandler`, `SeniorHandler` | Review API |
| The intake pipeline (Function or watcher) | App-only (`idtyp=app`) | `Intake.ReadHistory` on the API; `Claims.Read`, `Policies.Read` on the legacy system | `/claims/history`; legacy reads |
| The review API itself | App-only | `Claims.Write` on the legacy system | Legacy `POST /claims` |

The policies enforce the difference. A person's routes require `ClaimsHandler` or `SeniorHandler` **and refuse an
app-only token**, so a service identity can never act as a handler. An app policy requires the role **and
`idtyp=app`**, so a user token that happens to carry `Intake.ReadHistory` is still refused. The app roles are declared
`allowedMemberTypes: ["Application"]` in the manifests under `infra/entra/`, so Entra ID cannot assign them to a person
in the first place; the `idtyp` check is the second lock. `Claims.Write` goes only to the review API's identity: that
is what makes "no write without an approval" true of the mock too (the headline claim of ADR 0002).

Roles are **app roles on the token, not groups**: no Graph call, no group-overage problem, and the manifest is the
single list of what exists.

### 3. The reviewer is the token

The decision records the token's **`oid`** (the stable object id, the value that is stored and audited) and `name`
(display only, cleaned and length-capped, never used to decide anything). `reviewer` is removed from `ApproveRequest`
and `RejectRequest`. A body that still sends it gets a **400**, so an old client fails loudly and nobody believes the
field was used. `GET /me` returns the caller's id, name and roles for the UI.

### 4. Segregation of duties is a domain rule

`Claim.Approve(Approver, now)` takes an `Approver` (id, display name, roles) and decides:

| Claim's route | `ClaimsHandler` | `SeniorHandler` |
|---|---|---|
| `SeniorReview` | refused (`InsufficientRoleException`, HTTP 403, "Senior review required") | may approve |
| `StraightToHandler` | may approve | may approve |
| **No assessment** | may approve | may approve |

The rule lives in `ClaimsIntake.Domain`, so no endpoint, service or future consumer can bypass it, and it is tested
for every route and role pair. The service checks it before anything else, whatever the claim's state, so a caller who
may not approve a claim learns nothing else about it. A claim the assessor sent to senior review stays visible to
every handler, and **any handler may reject it** (see "Costs").

**A claim with no assessment** (no assessor ran: `Intake:Assessor=None`, or an older claim) **needs only
`ClaimsHandler`**. The review's open question was whether to require `SeniorHandler` when nothing assessed the claim.
We do not: absence of an assessment is not a finding. Blocking data-quality issues already stop approval whatever the
role, an assessor failure produces an explicit `SeniorReview` assessment (so it is not "no assessment"), and requiring
a senior for every claim in a deployment that has chosen not to run an assessor would make the senior role the default
approver and the distinction meaningless. If a customer wants the opposite, it is one line in
`Claim.RequiredApprovalRole`.

**A second approver above a financial limit** (discovery question Q6) is not modelled. The seam is the `Approver`
parameter and `RequiredApprovalRole`: a limit would add a second approver to the signature. It stays open until the
question is answered.

### 5. Every decision is audited, with the actor

Four records go to the claim's audit trail, next to the tool calls: `decision.approved`, `decision.rejected`,
`decision.submitted` and `decision.submit_failed`. Each holds the actor's `oid`, the role they acted under, the route
at the time, the outcome and (for submitted) the legacy reference. **No names and no free text**: a rejection records
the length of the reason, not the reason. The approval is written **before the legacy call**, so a legacy write
without an approval on record cannot happen; the G1 check is a test over the in-memory log.

Row keys are **deterministic** (`{ticks:D19}-{discriminator}`: the position in the trail for a tool call, the decision
kind for a decision) and writes are **upserts**, so a redelivered message or a retried approval rewrites the same rows
and never duplicates or loses one. A retry that finds a claim already approved or submitted writes the same records
again, which repairs a record lost to an earlier failure. A replayed approval therefore appends nothing new.

### 6. One gate for both decisions

Approve and reject run in one `ClaimDecisionService` under the same per-claim gate. A rejection that arrives while an
approval is writing to the legacy system waits, then finds the claim already decided and gets a 409. There is no
interleaving, no 500, and no claim in the legacy system that the review store shows as rejected (REL-03).

### 7. Local development: a dev sign-in that cannot exist elsewhere

Running locally with no tenant needs some way to be a handler. The dev scheme (`Auth:Mode=Dev`) accepts
`Authorization: Bearer dev-handler` (a `ClaimsHandler`), `dev-senior` (a `SeniorHandler`), and two service tokens
(`dev-app-pipeline`, `dev-app-api`) that the watcher and the API use between themselves. The tokens are fixed names, not
secrets, and mean nothing to a service running Entra. It is prevented from running outside Development in five layers,
each tested:

1. **Start-up refuses it.** `Auth:Mode=Dev` throws unless the hosting environment is exactly Development. There is no
   other switch. Tested for Production, Staging and Testing, on the API and on the legacy mock.
2. **Azure hosting refuses it.** Even if someone sets the environment name to Development on an Azure host, start-up
   throws when `WEBSITE_SITE_NAME`, `WEBSITE_INSTANCE_ID`, `FUNCTIONS_WORKER_RUNTIME`, `CONTAINER_APP_NAME` or
   `IDENTITY_ENDPOINT` is set. Tested for four of them.
3. **The handler checks again on every request**: the environment must be Development and the remote address must be
   loopback (an in-memory test server has none, and is allowed).
4. **The default mode is Entra.** `appsettings.json` says `Entra`. Only `appsettings.Development.json` says `Dev`. A
   Development-environment service with `Auth:Mode=Entra` rejects a dev token (tested).
5. **The service side cannot send a dev token outside Development.** `DevTokenHandler` throws when built outside
   Development, so a stray `DevPersona` setting in Azure fails the first call instead of sending a token (tested).

The UI shows a banner and a persona picker when no Entra configuration is present; it sends those tokens only in that mode.

### 8. The local hand-off route

`POST /intake/claims` (the watcher's local hand-off) is mapped only when `Intake:HttpEndpoint:Enabled` is true **and**
the environment is Development, and it then needs an app-only token with `Intake:Submit`. A stray app setting in Azure
leaves the route absent (404, tested).

### 9. Tokens between services

Typed `HttpClient`s carry an app-only token from a `DelegatingHandler` added after the resilience handler, so each retry
has one. On Azure `AppTokenHandler` gets it from `DefaultAzureCredential` (the managed identity) for a scope such as
`api://claims-intake-legacy/.default`, caching it until five minutes before it expires. Locally `DevTokenHandler` sends
the persona token. With neither configured nothing is sent, and the called service answers 401.

## Consequences

Good:

- The identity of every decision is the one in the token, and the audit trail can say who approved what in which role.
- A handler cannot approve what the assessor sent to senior review, and a compromised or confused caller cannot skip
  that by calling the API differently: the rule is in the domain.
- The legacy mock no longer accepts a write from anywhere but the review API's identity.
- It all runs and is tested offline: the tests send real JWTs through the real validation pipeline, and only the
  signing key and the issuer metadata are replaced with a test key.

Costs and limits:

- **Never run against Entra ID.** Token validation, the app-role claims, the `idtyp` claim (an optional claim that the
  manifest requests) and the app-only token flow are exercised with test tokens shaped like Entra's. Whether a real
  tenant issues exactly those claims is unverified. The Angular MSAL path is verified by mocked unit tests and the build.
- A token with **no roles and no scopes** is refused by `Microsoft.Identity.Web` with a 401, before our policies run, so
  "signed in but no role" is a 401 for a token with neither and a 403 for one with some other role.
- A token for another API's audience is a **401** (invalid token), not a 403.
- **Rejection is open to any handler**, including on a senior-review claim. That is deliberate: rejecting writes
  nothing, and a handler who sees a clear exclusion should be able to say no. It does mean a `ClaimsHandler` can end a
  claim that was flagged for a senior. If the customer's rules say otherwise, it is one check in `Claim.Reject`.
- The review store is still in memory (REL-01), so decisions are audited durably only when the Table Storage audit log is
  on; the claim state itself is lost on a restart. The audit write and the claim save are two steps, not a transaction:
  a crash between them leaves a record without a state change (harmless) or, for the reject path, a state change without
  its record. The durable store in Milestone 7 should write them together.
- The per-claim gate dictionary still grows by one entry per claim (REL-08, left open).
- **Group-based role assignment and Conditional Access** are not designed here. They are Entra configuration, not code.

## Alternatives considered

- **Roles from groups.** Needs a Graph call or the groups claim, which overflows past 200 groups. App roles are in the
  token and in the manifest.
- **Take the reviewer from the body but check it against the token.** The body field is then redundant at best and a
  confusion at worst. Removing it is simpler and fails loudly.
- **Require `SeniorHandler` when there is no assessment.** See above: it makes the senior role the default.
- **A separate "approver" service or a workflow engine for the two-person rule.** Right if the limits and approval chains
  grow. For one rule on one field it would be more machinery than the rule.
- **Mutual TLS or API keys between services.** Keys are exactly what ADR 0003 removed. App-only Entra tokens use the
  managed identity that is already there.
- **A dev scheme gated by a configuration flag only.** A flag is one setting away in a deployment. The environment check, the
  Azure-host check and the per-request check mean three independent mistakes would be needed.
