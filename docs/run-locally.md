# Running it locally, in detail

> The README has the short quickstart. This page covers each piece on its own and the Azure settings.

## Run the whole flow locally

Four processes, in four terminals (or run `./scripts/run-local.ps1` on Windows to start them all). Set
`ASPNETCORE_ENVIRONMENT=Development` (and `DOTNET_ENVIRONMENT=Development` for the watcher): that turns on the **dev sign-in**
([ADR 0009](adr/0009-identity-roles-and-segregation-of-duties.md)). It works only in Development. Every API call needs a
token: `dev-handler` is a claims handler, `dev-senior` a senior handler.

```bash
dotnet run --project src/ClaimsIntake.Legacy      # http://localhost:5200  mock claims system
dotnet run --project src/ClaimsIntake.Api         # http://localhost:5100  review API (Development: intake route on)
dotnet run --project src/ClaimsIntake.Watcher     # watches ./local-inbox
cd ui && npm start                                 # http://localhost:4200  review UI, proxies /api to the API
```

Then drop documents into the inbox and open http://localhost:4200:

```bash
cp samples/01-water-damage-clean.pdf samples/05-loss-outside-policy-period.pdf samples/06-email-fire.txt local-inbox/
curl -H "Authorization: Bearer dev-handler" "http://localhost:5100/claims?status=all"
```

To see the coverage assessment, drop the clean sample and one that triggers exclusions, then read the claim and its
audit trail (each sample states its own notification date, so the result does not depend on the day you run it):

```bash
cp samples/01-water-damage-clean.pdf samples/09-flood-office-exclusion.pdf samples/10-unoccupied-late-notification.pdf local-inbox/
cp samples/11-duplicate-water-damage-email.txt local-inbox/      # after 01 has been processed: a possible duplicate of it
curl -H "Authorization: Bearer dev-handler" http://localhost:5100/claims/<id>          # "assessment": findings with cited clauses, route, notes
curl -H "Authorization: Bearer dev-handler" http://localhost:5100/claims/<id>/audit    # tool calls (redacted arguments) and every decision with its actor
```

Sample 01 is covered under clause 3.2 and goes straight to a handler. Sample 09 cites the flood exclusion (4.1) of the
Office wording. Sample 10 cites the unoccupied exclusion (4.3) and the 30-day notification condition (5.1) and is
marked for senior review.

Each file moves to `local-inbox/processed/`, or to `failed/` if it cannot be read. In the UI, a handler opens a
claim, checks the fields against the source text, and approves or rejects it. Approving a claim with a blocking
error is refused. Approving a clean claim writes it to the legacy system and shows the legacy reference:

```bash
curl -X POST -H "Authorization: Bearer dev-handler" http://localhost:5100/claims/<id>/approve    # no body: the reviewer is the token
```

A claim the assessor routed to senior review (sample 09 or 10) answers **403** to `dev-handler` and works for
`dev-senior`. Without a token every route answers 401.

Approve it again and you get the same reference back with an `Idempotent-Replay: true` header, and the legacy system
still holds one claim. If the legacy system is down, the claim stays approved, the API answers 502, and approving
again completes it under the same idempotency key. See `samples/` for what each document is meant to show.

To use real Azure services instead, see the `Intake:*` settings in
[ServiceCollectionExtensions](../src/ClaimsIntake.Pipeline/ServiceCollectionExtensions.cs). They are untested against
the real services. For Milestone 3: `Intake:PolicySearch` (`Local` or `AzureAiSearch`, with
`Intake:AzureAiSearch:Endpoint` and `Intake:Foundry:EmbeddingDeployment`), `Intake:Assessor` (`Rules`, `Agent` or
`None`; `Agent` needs `Intake:Foundry:Endpoint` and `Deployment`), and on the API `Audit:Table:Endpoint`. To load the
wordings into the index (needs `az login` and the Search Service Contributor and Search Index Data Contributor
roles on the service):

```bash
dotnet run --project src/ClaimsIntake.Ingestion -- --wordings samples/policy-wordings --dry-run   # chunk and print, offline
dotnet run --project src/ClaimsIntake.Ingestion -- --wordings samples/policy-wordings \
  --search-endpoint https://<service>.search.windows.net --openai-endpoint https://<resource>.openai.azure.com \
  --embedding-deployment <name>
```

## The pieces separately

Run the review API (http://localhost:5100). To load four made-up claims for a demo without the watcher, set
`Seed__DemoData=true`.

```bash
dotnet run --project src/ClaimsIntake.Api

T="Authorization: Bearer dev-handler"            # or dev-senior
curl -H "$T" http://localhost:5100/claims                    # claims waiting for review, oldest first
curl -H "$T" http://localhost:5100/claims/<id>               # one claim: fields, confidence, source, issues
curl -X POST -H "$T" http://localhost:5100/claims/<id>/approve
curl -X POST -H "$T" -H "Content-Type: application/json" http://localhost:5100/claims/<id>/reject -d '{"reason":"Duplicate claim"}'
curl http://localhost:5100/health/ready
```

Run the mock legacy system (http://localhost:5200). In Development it accepts the dev service tokens, so add
`-H "Authorization: Bearer dev-app-api"` (write) or `dev-app-pipeline` (reads) to the calls below:

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
