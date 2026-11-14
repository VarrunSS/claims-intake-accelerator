# ADR 0004: Microsoft Agent Framework for the extraction agent

> Thames Mutual Insurance is a fictional customer. See [00-discovery.md](../00-discovery.md).

- Status: accepted (the agent itself is built in Milestone 3; see the Milestone 2 note below)
- Date: 2026-11-13

## Context

The extraction step needs a model that reads OCR text, decides which tools to call (`lookup_policy`,
`search_policy_wording`, `check_duplicate`, `create_claim`) and returns structured fields with confidence and
source spans. The customer's team writes C# and wants the agent code in .NET, hosted in the same solution as
the rest of the system. The model will be hosted on Azure AI Foundry in UK South, subject to open question 1.

Microsoft offers two .NET frameworks for this. Both sit on `Microsoft.Extensions.AI`, the shared abstraction
for chat clients and tools.

**Facts checked on NuGet on 13 November 2026:**

| | Microsoft Agent Framework | Semantic Kernel |
|---|---|---|
| Core package | `Microsoft.Agents.AI` | `Microsoft.SemanticKernel` |
| Latest stable | 1.28.0 (published 6 November 2026) | 1.82.0 |
| First stable release | 1.0.0 on 2 April 2026 | Several years ago |
| Targets | net10.0, net9.0, net8.0, netstandard2.0, net472 | Not checked (not needed for the decision) |
| Depends on | `Microsoft.Extensions.AI` 10.11.0 | Its own abstractions plus `Microsoft.Extensions.AI` adapters |
| Licence | MIT | MIT |
| Provider packages | For example `Microsoft.Agents.AI.OpenAI` | Connector packages |

Agent Framework has had 28 minor releases in about seven months. That is a fast cadence, so pinning and
reading release notes will be part of maintenance.

Microsoft has presented Agent Framework as the successor to the agent features of Semantic Kernel and to
AutoGen, built by the same teams, and recommends it for new agent work. Semantic Kernel is still released.

## Decision

Use **Microsoft Agent Framework** (`Microsoft.Agents.AI`) for the extraction agent, from Milestone 2.

Reasons:

1. **Direction.** It is the current Microsoft line for agents in .NET and has been generally available since
   April 2026. New work on Semantic Kernel's agent classes would be building on the older path.
2. **Fit.** The job is one agent with tools and structured output. Agent Framework models exactly that, with
   threads, tool calling and middleware, and leaves room for a workflow of several agents if we split
   extraction from policy checking later.
3. **Thin layer on shared abstractions.** Tools are plain C# methods exposed through
   `Microsoft.Extensions.AI` (`AIFunction`). That layer is common to both frameworks, so the tools, the
   prompts and the evaluation set carry over if we ever change.
4. **Observability.** It emits OpenTelemetry traces and metrics for model and tool calls, which we need for
   token, cost and latency measures (success metrics M4 and M5).
5. **Model choice.** It works through chat client abstractions, so Milestone 5's comparison of two or three
   Foundry models does not mean rewriting the agent.

Conditions:

- Wrap the agent behind our own interface (for example `IClaimExtractor`) in `ClaimsIntake.Domain` or a new
  `ClaimsIntake.Agent` project. The rest of the system never references the framework.
- **Pin the exact version** in `Directory.Packages.props` or the project file and update on purpose, with the
  evaluation gate (Milestone 4) as the safety net.
- The human-approval rule in [ADR 0002](0002-human-approval-for-writes.md) does not depend on any framework
  feature. The write path is outside the agent.
- Check the tool-approval API of the pinned version in Milestone 3 and use it as an extra control only.

## Consequences

Good:

- One framework, in the language the customer's team uses, on the path Microsoft is investing in.
- Tools and prompts are portable because they sit on `Microsoft.Extensions.AI`.

Costs:

- A young framework with a quick release cycle. Expect API changes between minor versions and some thin
  documentation. We mitigate this by pinning, wrapping and testing.
- Less community material and fewer worked examples than Semantic Kernel.
- If the customer already has Semantic Kernel code, there is a migration to consider. At Thames Mutual there is
  none (discovery, week 1).
- The agent will not be built in Milestone 1. This ADR records the choice so Milestone 2 can start straight
  away, and it should be revisited if the package's cadence or stability worsens.

## Milestone 2 note: `IChatClient` first, the agent in Milestone 3

Milestone 2 reads fields from a document in one model call: text in, structured fields out, no tools and no
thread. For that, `Microsoft.Extensions.AI` `IChatClient` with a JSON schema is enough, and it is the layer
Agent Framework itself sits on. So `FoundryLlmClaimExtractor` uses `IChatClient` directly, behind the
`IClaimExtractor` interface this ADR asks for, and `Microsoft.Agents.AI` is **not referenced yet**. Adding the
agent in Milestone 3 (policy lookup, wording search, duplicate check, `create_claim`) means a second
implementation behind the same interface, or this one wrapped by an agent; nothing outside the interface changes.
The decision above stands. The only deviation is timing: the framework arrives when there is a tool loop to
justify it, rather than for a single call. Package versions are pinned inline in each project file; the
`Directory.Packages.props` option mentioned above was not adopted.

## Alternatives considered

- **Semantic Kernel.** Mature, with a large body of examples, and still supported. Chosen if we needed
  something stable and boring today, or already used it. Rejected because the agent features are the part
  Microsoft has moved to Agent Framework, and we would start a new system on the path being replaced.
- **`Microsoft.Extensions.AI` directly, with our own tool loop.** The smallest dependency and complete
  control. We would write and maintain the tool-calling loop, thread state, retries and tracing ourselves.
  Reasonable for one agent, wasteful once we add a workflow. Kept as the fallback: because our tools already
  sit on this layer, falling back is cheap.
- **Python framework (LangGraph, AutoGen) in a separate service.** Strong ecosystem, but a second language and
  runtime for the customer to run and secure, and a network hop on the critical path. Rejected. Python is
  used only for the evaluation harness in Milestone 4.
- **Azure AI Foundry Agent Service (hosted agents).** Moves the loop and thread state into the platform.
  Attractive later. It reduces control over tool execution and the approval boundary, and the data-residency
  position in UK South is not confirmed. Deferred until question 1 is answered.
