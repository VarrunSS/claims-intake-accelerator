# Claims Intake Accelerator

A reference implementation of a document-to-claim pipeline for a UK commercial insurer, built on Azure with
.NET 10. It reads messy claim emails and PDFs, extracts the claim, checks it against the policy, and puts a
pre-filled claim in front of a handler. A person approves every write to the claims system.

> **Thames Mutual Insurance is a fictional customer.** The scenario, people, policies and numbers are invented
> for this portfolio project. No real claim data is used or needed.

It is run like a customer engagement: discovery notes, agreed success metrics, written design decisions, tests,
CI and infrastructure as code, with the work delivered in milestones.

## Status

Milestone 1 is done. The rest is planned and not started.

| # | Milestone | Status |
|---|---|---|
| 1 | Discovery and skeleton: docs, domain rules, review API, legacy mock, Function stub, Bicep, CI | **Done** |
| 2 | Happy path: PDF in Blob, Document Intelligence, extraction agent, claim in the Angular review UI | Not started |
| 3 | RAG and legacy tools: policy wording search with citations, write-back after approval, audit log | Not started |
| 4 | Evals and data quality: synthetic claims, scorer, eval gate in CI, unreadable-scan handling | Not started |
| 5 | Model comparison: 2 to 3 Foundry models on accuracy, cost and latency, written up as an ADR | Not started |
| 6 | Security and observability: split identities, PII redaction, threat model, App Insights workbook | Not started |
| 7 | Packaging: demo, runbook, final README and measured results | Not started |

**The infrastructure has not been deployed or validated.** `azd`, `az` and the Functions Core Tools were not
installed when Milestone 1 was written, so the Bicep in `infra/` has never been through `az bicep build`,
`what-if` or a deployment, and the Function App has never been started. Expect fixes on the first real
deployment. The .NET code is built and tested; see below.

## Architecture

The full Mermaid diagram and a component table are in [docs/architecture.md](docs/architecture.md). In short:

```
Blob inbox -> Event Grid -> Azure Functions -> Document Intelligence -> extraction agent
   -> Service Bus -> ASP.NET Core API -> Angular review UI -> mock legacy claims system
```

Design decisions are in [docs/adr](docs/adr):

1. [Event-driven pipeline](docs/adr/0001-event-driven-pipeline.md)
2. [Human approval for writes](docs/adr/0002-human-approval-for-writes.md)
3. [Managed identity, no keys](docs/adr/0003-managed-identity-no-keys.md)
4. [Microsoft Agent Framework for the agent](docs/adr/0004-dotnet-agent-framework.md)

Customer documents: [discovery](docs/00-discovery.md) and [success metrics](docs/01-success-metrics.md).

## What is built in Milestone 1

| Project | What it does |
|---|---|
| `src/ClaimsIntake.Domain` | `Claim`, `ExtractedField` (value, confidence, source span), `PolicyReference`, `ClaimStatus`, and `ClaimValidator`, which flags a missing policy number, an unknown policy, a missing, unreadable or future loss date, a loss date outside the policy period, an invalid amount and low-confidence fields. `Claim.Approve` refuses a claim with blocking errors. |
| `src/ClaimsIntake.Api` | Minimal API: list claims for review, get one, approve, reject. In-memory store behind `IClaimRepository`. Liveness and readiness health checks. OpenTelemetry traces and metrics with a configurable exporter. |
| `src/ClaimsIntake.Legacy` | Mock legacy claims system: `GET /policies/{number}` over 10 seeded policies, and `POST /claims`, which requires an `Idempotency-Key` header and makes retries safe. |
| `src/ClaimsIntake.Functions` | .NET 10 isolated worker with a blob-trigger stub that logs the document and puts a message on a Service Bus queue. It compiles; it has not been run. |
| `tests/` | 101 xUnit tests: validation rules and claim state changes, every API endpoint through `WebApplicationFactory`, and legacy idempotency including concurrent retries. |
| `infra/`, `azure.yaml` | Bicep for `azd up`: managed identity with role assignments, storage, Service Bus, Key Vault, Log Analytics and Application Insights, a Function App and an App Service plan for the API and the mock. |

## Quickstart

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Nothing else, and no Azure account.

```bash
dotnet build
dotnet test
```

Run the review API (http://localhost:5100). In the Development environment it loads four made-up claims.

```bash
dotnet run --project src/ClaimsIntake.Api

curl http://localhost:5100/claims                    # claims waiting for review, oldest first
curl http://localhost:5100/claims/<id>               # one claim: fields, confidence, source, issues
curl -X POST http://localhost:5100/claims/<id>/approve \
     -H "Content-Type: application/json" -d '{"reviewer":"a.patel"}'
curl -X POST http://localhost:5100/claims/<id>/reject \
     -H "Content-Type: application/json" -d '{"reviewer":"a.patel","reason":"Duplicate claim"}'
curl http://localhost:5100/health/ready
```

Run the mock legacy system (http://localhost:5200):

```bash
dotnet run --project src/ClaimsIntake.Legacy

curl http://localhost:5200/policies/TM-CP-100001
curl -i -X POST http://localhost:5200/claims \
     -H "Content-Type: application/json" -H "Idempotency-Key: demo-1" \
     -d '{"policyNumber":"TM-CP-100001","claimantName":"Brindle & Marsh Joinery Ltd","lossDate":"2026-11-03","description":"Burst pipe","estimatedAmountGbp":12500}'
```

Send the same request again with the same key and you get the original claim back with an
`Idempotent-Replay: true` header. Use the same key with a different body and you get a 409.

Telemetry: set `Telemetry__Exporter` to `console` (default), `otlp` (honours the standard
`OTEL_EXPORTER_OTLP_ENDPOINT` variable) or `none`.

Coverage, as CI runs it: `dotnet test --collect:"XPlat Code Coverage"`.

## Security posture

What is in place in Milestone 1, and what is not.

In place:

- **No keys or connection strings.** The Bicep gives one user-assigned managed identity scoped role
  assignments, turns off shared-key access on storage and local auth on Service Bus, and puts only resource
  names in application settings ([ADR 0003](docs/adr/0003-managed-identity-no-keys.md)).
- **Human approval for writes.** The agent can only propose. The domain model refuses to approve a claim that
  has data-quality errors or has already been decided ([ADR 0002](docs/adr/0002-human-approval-for-writes.md)).
- **UK South only.** The Bicep `location` parameter allows only `uksouth`.
- **Safe retries.** The legacy write needs an idempotency key. Reuse with a different body is refused.
- **Input handling.** Request bodies are validated and length-limited. Blob names are cleaned before logging.
- **Defaults on the hosts.** HTTPS only, TLS 1.2 minimum, FTP off, basic publishing credentials off,
  Key Vault with RBAC, soft delete and purge protection, storage with no public blob access.

Not yet in place (planned, mainly Milestone 6):

- The review API has **no authentication**. The reviewer name comes from the request body. Entra ID sign-in and
  roles come next. Do not expose the API as it stands.
- The mock legacy system has no authentication either. It stands in for a system that would have its own.
- One identity is shared by all three apps. It will be split by component.
- No private endpoints or network restrictions. No Azure Policy. No PII redaction in telemetry (nothing logs
  claim content today, but nothing enforces it yet). No threat model.
- Application Insights still accepts connection-string ingestion.
- Nothing has been deployed, so none of the Azure configuration has been tested.

## What was not verified

- The Bicep has not been compiled with `az bicep build`, checked with `what-if`, or deployed. In particular,
  these are unconfirmed: .NET 10 on Flex Consumption (`dotnet-isolated`, `10.0`), `DOTNETCORE|10.0` on
  Linux App Service in UK South, the `azd` `host: function` setting for a Flex plan, the Function host
  settings for identity-based blob trigger, Service Bus and host storage connections, and azd deployment with
  basic publishing credentials turned off. Role definition ids were checked against Microsoft's published
  built-in roles list.
- The Function App has not been started, because the Functions Core Tools are not installed. The project
  builds, and the Event Grid blob trigger is only compiled, not run.
- The Mermaid diagram was parsed by the Mermaid library but not viewed in a renderer.

## Repository layout

```
.github/workflows/ci.yml   build, test with coverage; eval job commented out until Milestone 4
docs/                      discovery, success metrics, architecture, ADRs
infra/                     Bicep (main.bicep and modules), azd parameters
src/                       Domain, Api, Legacy, Functions
tests/                     Domain.Tests, Api.Tests (API and legacy)
azure.yaml                 azd project definition
```

## Licence

MIT. See [LICENSE](LICENSE).
