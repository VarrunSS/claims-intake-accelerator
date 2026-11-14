# Known limitations and what was not verified

## Known limitations / next steps

Left on purpose, documented, and not hidden. Each has a finding ID in the [review](reviews/2026-11-architecture-security-review.md).

- **A durable review store (REL-01), the first thing to build next.** The store is in memory: an API restart loses claims awaiting review, and the API is
  pinned to one instance. A Table Storage or Cosmos repository with ETags is next.
- **The claim-check pattern (REL-02).** Large documents are cut to under 200 KB with a blocking issue; the document goes in blob storage and a reference on the queue is the proper fix.
- **Private networking (INF-03).** Public endpoints with Entra-only authentication; private endpoints need Service Bus Premium (about GBP 550 a month). [ADR 0011](adr/0011-network.md).
- **Prompt Shields (AI-03).** The `IPromptAttackDetector` seam is a no-op; Azure AI Content Safety was never integrated or run.
- **Defender for Storage malware scanning (INP-02).** The magic-byte check is a sniff, not a scan.
- **Hidden-text detection (AI-04).** The handler sees extracted text, which can contain text a person cannot see in the rendered page.
- **A second approver above a financial limit (SEC-03, discovery Q6).** The seam exists; the rule awaits an answer.
- Also open: a way back from a legacy refusal (REL-08), purge of audit records and dead letters ([ADR 0010](adr/0010-retention.md)), the deployment workflow with OIDC (SC-02: none exists), a Document Intelligence and Foundry model in the default deployment.

## What was not verified

- **Milestone 6a, offline only:** Entra ID token validation, app-role and `idtyp` claims, the app-only token flow with
  `DefaultAzureCredential`, the MSAL Angular sign-in (mocked unit tests and the build only), and the Entra app
  registration manifests have never met a real tenant. Test tokens are signed with a symmetric test key and shaped like
  Entra's. The agent hardening is proven with a scripted model, not a real one. The JSON-encoded extraction prompt has
  not been tried with a real model: how well a model quotes verbatim from JSON-escaped text is unknown. The eval
  `injection` cases measure the offline rules assessor, not a model. Prompt Shields was never run.

- **Milestone 6b.** The Bicep (`infra/`) compiles and lints clean with the standalone Bicep CLI 0.49.2 (checksum verified against the
  GitHub release digest), and the role-matrix assertion and its tests pass against the compiled ARM. `az bicep build/lint` and PSRule for
  Azure **will first run in CI**; PSRule was never run (suppressions in `ps-rule.yaml` are from reading the rules, so some may be wrong), the
  workflows were checked with actionlint 1.7.12 and never ran on GitHub, and `dependabot.yml` and `ps-rule.yaml` were only checked by eye (no YAML parser was available). Nothing was deployed or run
  through `what-if`. Unconfirmed on Azure: that the roles are enough at run time (the blob trigger with Blob Data Owner on the `inbox`
  container only, Flex host storage), the Event Grid webhook with the `blobs_extension` key and the two-pass deployment, dead-lettering
  with the topic identity, Azure Monitor ingestion with Entra ID, the Functions host in OpenTelemetry mode with the pipeline source, that
  Azure SDK Service Bus spans do not duplicate the explicit `traceparent`, the alert queries (they assume how counters and span tags land in
  `customMetrics` and `requests`), the workbook, the lifecycle policy, the `use-recent-api-versions` rule being off, and `NuGetAudit` behaviour on the
  GitHub runner. The CI jobs `iac`, `sbom`, `dependency-review` and CodeQL have never run. Rate limits are in memory, per instance.
  The local smoke run proved one trace id from the watcher to the API over HTTP; the Service Bus propagation is proven with fakes only.
- The Bicep before Milestone 6b had never been through a compiler; it now has, but never `what-if` or a deployment. In particular,
  these are unconfirmed: .NET 10 on Flex Consumption (`dotnet-isolated`, `10.0`), `DOTNETCORE|10.0` on
  Linux App Service in UK South, the `azd` `host: function` setting for a Flex plan, the Function host
  settings for identity-based blob trigger, Service Bus and host storage connections, and azd deployment with
  basic publishing credentials turned off. Role definition ids were checked against Microsoft's published
  built-in roles list.
- **Milestone 3 Azure paths, compiled and tested only against fakes, never run:** `AzureAiSearchPolicyWordingSearch`
  (the keyword and vector query, options, filter and result mapping are tested; `SearchClientClauseQuery` and
  `SearchIndexClauseWriter`, which call the SDK, are not), the index schema (checked as an object, never created),
  the ingestion tool's upload path, `AgentClaimAssessor` against a real model (tested with a scripted `IChatClient`,
  so how a real model behaves with the prompt, the tools and the JSON answer is unknown), `TableStorageAuditLog`
  (against a fake table; `TableClientAuditTable` is not tested), and the new Bicep (an Azure AI Search resource, the
  Search Index Data Reader role, the audit table and app settings, none of it through `az bicep build`). The Foundry
  chat and embedding deployments are only in the Bicep as an optional parameter (Milestone 5, off by default), so by default the Function App is configured for the local search and
  the rule-based assessor. Whether the free Azure AI Search tier accepts Entra ID only access is assumed, not checked.
- The rule-based assessor was tuned on the eleven samples, so its results on them flatter it. Retrieval quality on
  real, longer wordings is unmeasured. The assessment view was looked at in a real browser for the README screenshots (see the last UI item below).
- The Function App has not been started, because the Functions Core Tools are not installed. The project
  builds, the function is unit-tested by calling it directly with fakes, and the Event Grid blob trigger is only
  compiled, not run. The Function's OpenTelemetry wiring (Milestone 6b) is compiled only.
- **Milestone 2 Azure paths, compiled and tested only against fakes, never run:**
  `AzureDocumentIntelligenceExtractor` (its `DocumentIntelligenceClient` adapter is not even unit-tested),
  `FoundryLlmClaimExtractor` against a real model (the JSON schema is hand-written and not checked against a live
  service's strict-mode rules), `ServiceBusClaimSink`, the API's `ServiceBusClaimConsumer`, and the new Bicep app
  settings. The Document Intelligence resource is not in the Bicep, and the Foundry account is optional (`deployFoundry`, off by default, added in Milestone 5), so a default deployment still runs the local PDF reader, the heuristic extractor and the rules assessor. Extraction quality on real
  documents or with a real model is unmeasured; the Milestone 4 evaluation set (58 cases now) is synthetic.
- The heuristic extractor was tuned on the eight synthetic samples in this repository, so its results on them
  flatter it.
- The UI is tested in jsdom. For Milestone 7 it was also loaded in headless Microsoft Edge against the locally running stack with the dev sign-in, and screenshots were taken of the claim list, claim detail, cited clauses and audit trail. The MSAL sign-in path was not run in a browser. It has no
  automated browser or accessibility test.
- The Mermaid diagrams (architecture.md and the README) were rendered with Mermaid 11.4.1 in headless Edge for Milestone 7 and drew without errors. They have not been viewed on GitHub itself.
