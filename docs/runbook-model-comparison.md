# Runbook: compare models on a real Azure AI Foundry project

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](00-discovery.md).

**Status: never run.** The comparison harness was built and tested against scripted fakes, in an environment with no Azure
account and no AI credentials. Every step below that touches Azure is written from documentation and has not been tried.
Expect to correct a command or a property name on the first run, and fix this page when you do.

Method and decision rules: [ADR 0008](adr/0008-model-selection-and-tuning.md). Harness: [evals/README.md](../evals/README.md).

## What you need

- An Azure subscription with permission to create a Foundry resource, and quota for the models you pick in a UK region
  (UK South is the project's data-residency rule). Quota and model availability differ by subscription: check them in the
  Foundry portal first.
- The .NET 10 SDK and the Azure CLI (`az`). Nothing else: the evaluation uses the local PDF reader and the local policy
  search, so Document Intelligence and AI Search are **not** needed or charged.
- Synthetic data only. The dataset in `evals/dataset` is synthetic. Do not point this at real claims until the DPIA question
  in the discovery notes is answered.

## 1. Sign in

```bash
az login
az account set --subscription "<your subscription id>"
```

The harness authenticates with `DefaultAzureCredential`, which picks up this sign-in. There are no keys to copy and none
may be added to `models.json`.

## 2. Create the Foundry resource and give yourself a role

Either create an **Azure AI Foundry** resource (kind `AIServices`) in the portal, or use the Bicep in
`infra/modules/foundry.bicep` (not validated; see its header). Note the resource name.

Your own sign-in needs a data-plane role on the resource, **Cognitive Services User** (it covers model inference and
Claude), and **Cognitive Services OpenAI User** for Azure OpenAI deployments. The newer **Azure AI User** role is the
Foundry-specific equivalent and may be offered in the portal instead; use whichever your tenant lists. Contributor or Owner on
the resource does not grant data-plane access to models when local auth is disabled.

```bash
az role assignment create --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --role "Cognitive Services User" \
  --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<resource-name>"
```

Role assignments can take several minutes to take effect. A 401 or 403 in the first run usually means wait, or the wrong scope.

## 3. Deploy the three models

In the Foundry portal (Model catalogue, then Deploy), create one deployment each for:

| Registry id | What | Kind in `models.json` |
|---|---|---|
| `gpt-mid` | A mid-size GPT-family model you have quota for | `azure-openai` |
| `claude-sonnet` | A Claude model (Sonnet class) from the Foundry catalogue. Accept the Anthropic terms when asked | `foundry-anthropic` |
| `small-cheap` | A small, cheap catalogue model, for example a Phi-class model | `azure-ai-inference` |

Check before you pick: the extractor needs **JSON-schema response format** and the agent assessor needs **tool calling**.
A model without tool calling can only be compared on extraction; a model without structured output will show up as a high
malformed-output rate, which is the harness doing its job. Note each deployment name exactly.

CLI equivalent for an Azure OpenAI deployment (untested; model name, version and SKU are yours to fill in):

```bash
az cognitiveservices account deployment create -g <rg> -n <resource-name> --deployment-name gpt-mid \
  --model-format OpenAI --model-name "<model name>" --model-version "<version>" --sku-name GlobalStandard --sku-capacity 30
```

## 4. Fill in `evals/models.json`

For each model set `endpoint`, `deployment` and `enabled: true`, and **check the price**:

- `azure-openai`: `endpoint` is `https://<resource-name>.openai.azure.com/`, `deployment` is the deployment name.
- `azure-ai-inference`: `endpoint` is `https://<resource-name>.services.ai.azure.com/models`, `deployment` is the model or
  deployment name the endpoint expects.
- `foundry-anthropic`: `endpoint` is the Foundry **resource name** (not a URL) and `deployment` is the Claude model name.

Prices are GBP per 1M tokens, and the three shipped are **placeholders marked `verify-before-use`**. Look up the current
price for your deployment type on the provider's page (Azure OpenAI pricing for the GPT model; Anthropic's pricing for Claude,
because Foundry bills it as Claude Consumption Units at Anthropic's token rates; the Foundry pricing page for catalogue
models), convert to GBP, put the page in `sourceUrl`, today's date in `asOf`, and only then set `status` to `verified`.
The cost columns carry a `*` until you do.

## 5. Run the comparison (one command)

From the repository root:

```bash
dotnet run --project evals/ClaimsIntake.Evals -c Release -- compare --split test --prompts v1,v2 --run-id first-real-run
```

This runs the heuristic baseline plus every enabled model with prompt v1 and v2 over the 36-case test split, and writes
`evals/results/first-real-run/comparison.md`, `comparison.json` and a folder per configuration with `report.md` and
`results.json`. Useful options: `--models gpt-mid,claude-sonnet` to pick models, `--parallelism 2` if you hit rate limits,
`--split dev` for a cheaper first try (15 cases). Tune on `dev`; the test split is for the number you report.

Run a single configuration, and check its gate, with:

```bash
dotnet run --project evals/ClaimsIntake.Evals -c Release -- --extractor llm --assessor agent --model gpt-mid --prompt v2 --split dev
```

A model that is still `enabled: false` is listed as "not yet run" and costs nothing. Exit code 2 with a message means it could
not start (for example a placeholder endpoint).

## 6. How to read the report

- **Banner.** It says "Not yet run against real models" until at least one real model row has numbers. If you see it after a
  run, no model row ran: look at the "could not start" reasons in the table.
- **Results table.** Field F1, route accuracy, citations that exist and quote the wording exactly, citations the guard
  removed, malformed rate, p50 and p95 latency per claim, £ per claim and per 1,000, and a gate verdict. Compare each model
  with the **baseline** row, which is the floor: a model that is no better than the heuristics is not worth its cost.
- **Model call detail.** Tokens, 429 retries, content-filter refusals, failed calls, pipeline errors and malformed or
  schema-violating replies. A high retry count means the run was rate-limited and its latency includes the waiting.
  A pipeline error is a claim that produced nothing (a refusal, or a call that failed after its retries).
- **Recommendation.** Generated by the rules in the file. It names every candidate and why it was or was not eligible. "No
  recommendation" is a valid outcome, and usually means the reliability bounds in `evals/model-thresholds.json`, which have
  never been measured against a model, need a look. Change them in a commit that says why.
- **What the numbers are.** 36 synthetic claims written by one author. One case is about 3 points of route accuracy. Treat close
  quality as a tie, re-run before relying on a pick, and remember that latency is end to end with several claims in flight.
- **Guard removed.** A model that often cites clauses it was never shown is a model to be careful with, even though the
  guard keeps the output safe.

Then record the outcome: copy the table into ADR 0008 under a dated "Result" heading, set `status: verified` prices, update
the README status table, and commit `comparison.md` (not the per-configuration folders, which are git-ignored).

## 7. Expected cost of one run

**This is an estimate, from token estimates and placeholder prices. It is not a measurement.** The first real run reports
actual tokens and cost; use that.

Token estimate for one claim that reaches the model (my estimates for this prompt and these tools, not counted):

| Stage | Calls | Input tokens | Output tokens | Basis |
|---|---|---|---|---|
| Extraction | 1 | about 900 (v1) to 1,500 (v2) | about 250 | System prompt 130 or 700, JSON schema about 250, a 1 to 2 page document about 500 |
| Assessment (agent) | about 4 turns | about 11,000 in total | about 600 in total | Each turn re-sends the instructions (about 500), tool definitions (about 350) and the conversation so far; the three wording searches return up to 5 clauses of about 120 tokens each |

So about 12,000 input and 850 output tokens per claim. Over the 36 test cases, treating all of them as reaching the model (the
input-quality gate stops a few, so this is an upper bound): **about 430,000 to 460,000 input and 31,000 output tokens per
configuration**.

| Placeholder price (GBP per 1M, in / out) | Cost of one configuration over 36 claims | Per 1,000 claims |
|---|---|---|
| 1.60 / 6.40 (GPT placeholder) | about £0.93 | about £26 |
| 2.30 / 11.50 (Claude placeholder) | about £1.40 | about £39 |
| 0.10 / 0.40 (small placeholder) | about £0.06 | about £1.60 |

One prompt across all three models is about **£2.40**; v1 and v2 across all three is about **£5**, and a few pounds more
if retries or longer tool loops add turns. Sensitivity: each £1 per 1M input tokens changes one configuration's run by about
£0.46, and each £1 per 1M output tokens by about £0.03, so input tokens dominate because of the agent's repeated tool turns.
Real prices can differ from the placeholders by a multiple. There is no Document Intelligence or AI Search charge in this run.
At these placeholder prices a claim costs well under the M4 target of £0.60; that conclusion is only as good as the prices.

## 8. Switching the pipeline over (after you have a pick)

Set, on the Function App (or in `local.settings.json` locally):

```
Intake__ClaimExtractor = Foundry
Intake__Assessor       = Agent
Intake__Foundry__Endpoint   = https://<resource-name>.openai.azure.com/
Intake__Foundry__Deployment = <the picked deployment>
Intake__PromptVersion       = v1            # or the version the comparison picked
```

The Bicep wires these from the `claimExtractor`, `assessor`, `promptVersion`, `deployFoundry` and `foundryDeployments`
parameters, and gives the managed identity Cognitive Services OpenAI User and Cognitive Services User on the account. The
production client is the Azure OpenAI client, so the pipeline path supports the `azure-openai` kind; a Claude or catalogue
model pick needs a small change to `ServiceCollectionExtensions` to build its client the way `ChatClientFactory` does. The
setting names are `Intake:ClaimExtractor` and `Intake:Assessor`, as the code already has them.

## Cleaning up

Deployments bill per token and have no idle charge for standard deployments, but check your SKU. Delete the Foundry resource
when you are finished: `az cognitiveservices account delete -g <rg> -n <resource-name>`, then purge it if you need the name back.
