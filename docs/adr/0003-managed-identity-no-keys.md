# ADR 0003: Managed identity everywhere, no keys or connection strings

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](../00-discovery.md).

- Status: accepted
- Date: 2026-11-13

## Context

The system connects services to each other: Functions to Blob Storage and Service Bus, the API to Service Bus
and the legacy system, and later the agent to the model, Document Intelligence and AI Search. Each connection
needs a credential. Shared keys and connection strings are the usual shortcut. They are long-lived, they end
up in settings files, build logs and chat messages, they are hard to rotate, and anyone holding one can use it
from anywhere. The customer's security team has said that secrets in application settings will not pass review.

Azure services support Microsoft Entra ID authentication with managed identities, which gives a workload a
short-lived token with no secret to store.

## Decision

1. **Services authenticate to each other with managed identity and Azure RBAC.** No account keys, SAS tokens,
   connection strings with secrets, or API keys in code, configuration, pipelines or Bicep outputs.
2. **Shared-key and local authentication are switched off** on the resources that support it: storage accounts
   (`allowSharedKeyAccess: false`), Service Bus (`disableLocalAuth: true`), the Log Analytics workspace.
   Application Insights has `DisableLocalAuth: true` (Milestone 6b): every exporter signs in with its managed
   identity and holds Monitoring Metrics Publisher.
3. **Configuration names a resource, not a secret.** For example `ServiceBus__fullyQualifiedNamespace` and
   `InboxStorage__serviceUri`, with `__credential=managedidentity` and `__clientId` for the user-assigned
   identity. Code uses `DefaultAzureCredential` or the framework's identity-based connection settings.
4. **Least privilege, per resource.** Role assignments are scoped to the single storage account, namespace or
   vault, and use built-in roles that match the job (for example Service Bus Data Sender and Receiver, not
   Owner). They are declared in `infra/modules/roles.bicep`.
5. **Key Vault is for anything that cannot avoid a secret** (for example a third-party key if one is ever
   needed). It uses RBAC, soft delete and purge protection. Apps read it by managed identity. None is stored in
   Milestone 1.
6. **People are not given standing data access.** Developers use their own Entra ID account with roles
   granted for the environment. Shared-key access is switched off in the templates; no Azure Policy assignment enforces
   it, so a resource created outside the templates is not covered (corrected in Milestone 6b, DOC-01 b).
7. **Deployments will use OIDC federation** from GitHub Actions to Azure, with no stored client secret. There is no
   deployment workflow yet (SC-02, DOC-01 c), so this is a decision, not a fact. Nothing has been deployed.

## Consequences

Good:

- No secret exists to leak, so there is nothing to rotate, and nothing in the repository for a scanner to find.
- Access is auditable by identity and revocable by removing a role assignment.
- Matches what the customer's security review asks for, and what Microsoft documents for these services.

Costs:

- Local development cannot use keys. Developers sign in with `az login` or Visual Studio and need roles on a
  development environment, or use in-memory substitutes. This repository keeps stores behind interfaces
  so tests need no Azure access.
- RBAC role assignments can take minutes to take effect, which can make a first deployment flaky. The
  deployment and runbook will say so.
- Some features still need a shared key or a workaround (for example, certain trigger types on older
  plans). We choose the Flex Consumption plan, which supports identity-based host storage. Any exception
  needs an ADR of its own.
- **One identity for everything in Milestone 1.** The Function App and both web apps share a single
  user-assigned identity. That is simple but broader than ideal: the API has the Function App's storage
  rights. Milestone 6b split it into one identity per component (Function, API, legacy mock) with resource-scoped roles;
  `infra/role-matrix.expected.json` is the contract and CI asserts it.
- Mistakes in role assignments fail at run time, not deployment time. The Bicep in this repository has not
  been deployed yet (see README), so the role set is unproven.

## Alternatives considered

- **Connection strings in Key Vault, read by managed identity.** Better than plain settings, but there is
  still a long-lived secret, and rotation is a process someone must run. Rejected for service-to-service calls.
- **System-assigned managed identities.** Simpler, one per resource, and they disappear with the resource.
  A user-assigned identity can have its role assignments created before the apps that use it, which avoids a
  race in the deployment, and it can be shared while we are small. We use user-assigned and will separate
  them later.
- **Service principals with client secrets or certificates.** Needed outside Azure, but they bring back
  secrets to store and rotate. Not needed here. Rejected.
- **SAS tokens for browser access to documents.** Possible for the review screen later. If used, they would be
  short-lived user-delegation SAS issued by the API, not account keys. To be decided in Milestone 2.

## Addendum (Milestone 6b): the `blobs_extension` key is the one exception

The blob trigger uses the Event Grid source, and Event Grid delivers to the Function through a webhook whose URL needs the
Function's `blobs_extension` system key (`.../runtime/webhooks/blobs?functionName=...&code=<key>`). That is a shared secret
stored in the event subscription, so it is an exception to "no keys". It is not a credential for any data: it only lets Event
Grid call that one webhook, and it can be rotated by rotating the key and redeploying the subscription stage
(`infra/modules/eventgrid-subscription.bicep`). The key exists only after the Function code is deployed, which is why the
subscription is a second deployment pass (`deployEventSubscription`). Undelivered events are retried, then dead-lettered to a
container with the topic's own identity (Blob Data Contributor on that container), and an alert fires. Never run on Azure.
