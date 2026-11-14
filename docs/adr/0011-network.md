# ADR 0011: Network exposure: public endpoints now, private networking deferred

- Status: accepted (Milestone 6b). Closes INF-03 as a documented decision. Nothing here has been deployed.

## Context

Every resource today has a public endpoint: storage (inbox, host, audit), Service Bus, Key Vault, Azure AI Search, Foundry, the Function
App and the two web apps. All data-plane access needs Entra ID: shared keys are off on storage, local auth is off on Service Bus, Search,
Foundry, Log Analytics and Application Insights, and the apps validate Entra tokens. A Microsoft security reviewer would still ask for
private networking, so the choice is recorded.

## Decision

Keep public endpoints with Entra-only authentication for the pilot. Defer private networking.

The target design, when there is a customer environment: a VNet with a subnet for App Service and Flex Consumption VNet
integration; private endpoints for storage (blob, queue, table), Key Vault, AI Search and Foundry; Service Bus **Premium** with a private
endpoint; `publicNetworkAccess: Disabled` on all of them; private DNS zones; the CI runner and developers reaching the data plane through a
jump host or a self-hosted runner.

## Why Service Bus Premium is the blocker, and what it costs

Service Bus **Standard** cannot have private endpoints. Only **Premium** can. Premium is billed per messaging unit: about **GBP 550 a
month for one messaging unit** (a figure to confirm against the current price list), against roughly GBP 8 a month for Standard at this
volume. The rest of the private design (private endpoints at about GBP 6 a month each, the DNS zones, a VNet-integrated App Service plan
above Basic) adds a smaller amount on top. For a pilot that handles tens of claims a day, the Service Bus line alone is about two orders
of magnitude over the rest of the bill, so this is a cost decision for the customer, not a code change.

## Consequences

- The template has no firewall rules beyond what the platform gives. Network rules by IP or service tag were considered and rejected
  for now: the callers are Azure services with changing addresses, so rules would be wrong or empty.
- PSRule network rules are excluded in `ps-rule.yaml`, each pointing here.
- Key Vault is empty and no identity has a role on it (INF-03): there is nothing to protect with a network rule yet.
- The review API and the legacy mock are public hosts. The legacy mock stays a mock: in the real world it sits behind the customer's
  firewall. The API is protected by Entra ID and rate limits, not by the network.
- Residual risk: a leaked token is usable from anywhere. Conditional Access (device compliance, location) on the tenant is the
  compensating control to ask the customer for.
