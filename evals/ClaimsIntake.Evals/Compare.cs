using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaimsIntake.Evals;

public enum RowKind
{
    /// <summary>The heuristic extractor and rules assessor: the floor a model has to justify its cost against. Never a candidate.</summary>
    Baseline,

    /// <summary>A real model that was run.</summary>
    Model,

    /// <summary>A fake model: a pipeline demonstration, never a model result.</summary>
    Fake,

    /// <summary>A registry entry that has not been deployed (or whose run could not start). No numbers.</summary>
    NotRun,
}

/// <summary>One row of the comparison table.</summary>
public sealed record CompareRow(string Label, RowKind Kind, string Model, string Prompt, EvalSummary? Summary, GateResult? Gate, string? Reason, bool PriceUnverified)
{
    public double? CostPerClaim => Summary?.Metrics.GetValueOrDefault("cost_per_claim_gbp");

    public double? P95Ms => Summary?.Latency.FirstOrDefault(l => l.Stage == Summariser.Total)?.P95Ms;

    public double? P50Ms => Summary?.Latency.FirstOrDefault(l => l.Stage == Summariser.Total)?.P50Ms;

    public bool PassedGates => Gate is { Passed: true };
}

/// <summary>The outcome of the decision rules.</summary>
/// <param name="Chosen">The label of the config picked, or null if none qualifies.</param>
/// <param name="Demonstration">True when the pick is among FAKE rows only. It shows the rules work; it is not advice.</param>
public sealed record Recommendation(string? Chosen, bool Demonstration, IReadOnlyList<string> Explanation);

/// <summary>
/// Picks a configuration from measured rows by fixed rules. Nothing here is judgement: the same rows always give the same pick,
/// and the explanation lists every candidate and why it passed or not. The rules are repeated in ADR 0008; change both together.
/// </summary>
public static class Recommender
{
    /// <summary>Two costs closer than this (in GBP per claim) are a tie.</summary>
    public const double CostTieGbp = 0.0001;

    /// <summary>Two p95 latencies closer than this (in ms) are a tie.</summary>
    public const double LatencyTieMs = 1.0;

    public static readonly IReadOnlyList<string> Rules =
    [
        "**Candidates** are model configurations that were actually run (model and prompt version). The heuristic baseline is the reference, not a candidate. A registry entry that has not been run is not a candidate. FAKE rows are considered only when no real model was run, and then the pick is labelled a demonstration.",
        "**Gate.** A candidate must pass every threshold in the gate file (by default `evals/model-thresholds.json`): field F1 and per-field F1, issue and finding precision and recall, route accuracy, citation groundedness, silent-error rate, no pipeline errors, a malformed-output rate and a p95 latency ceiling. A metric that was not measured fails its threshold.",
        "**Cost.** A candidate must have a measurable cost per claim (the model reported token usage and the registry has a price).",
        "**Pick** the cheapest passing candidate by cost per claim.",
        "**Ties** (costs within £0.0001 per claim) go to the lower p95 latency per claim (within 1 ms counts as equal), then the lower malformed-output rate, then the label in alphabetical order.",
        "**No pick.** If no candidate passes, there is no recommendation: the report names the candidates and the gates they failed.",
        "**Caveats are attached, not applied.** If the chosen price is marked verify-before-use the recommendation says so. The rules never use judgement about the text of an answer.",
    ];

    public static Recommendation Recommend(IReadOnlyList<CompareRow> rows)
    {
        var real = rows.Where(r => r.Kind == RowKind.Model && r.Summary is not null).ToList();
        var fake = rows.Where(r => r.Kind == RowKind.Fake && r.Summary is not null).ToList();
        var demonstration = real.Count == 0;
        var candidates = demonstration ? fake : real;
        var notes = new List<string>();

        if (candidates.Count == 0)
        {
            notes.Add("No model has been run, so there is nothing to recommend. This is not yet run against real models.");
            return new Recommendation(null, false, notes);
        }

        if (demonstration)
        {
            notes.Add("**FAKE: pipeline demonstration, not a model result.** No real model was run; the rules below were applied to the fake rows only, to show that they work.");
        }

        var eligible = new List<CompareRow>();
        foreach (var c in candidates.OrderBy(c => c.Label, StringComparer.Ordinal))
        {
            if (!c.PassedGates)
            {
                var failed = c.Gate is null ? "no gate result" : string.Join(", ", c.Gate.Failures.Select(f => $"`{f.Metric}` ({f.Reason})"));
                notes.Add($"- `{c.Label}`: not eligible, failed {failed}.");
            }
            else if (c.CostPerClaim is null)
            {
                notes.Add($"- `{c.Label}`: not eligible, cost per claim could not be measured (no token usage or no price).");
            }
            else
            {
                notes.Add($"- `{c.Label}`: passes every gate, £{Money(c.CostPerClaim)} per claim, p95 {Ms(c.P95Ms)} ms.");
                eligible.Add(c);
            }
        }

        if (eligible.Count == 0)
        {
            notes.Add("No candidate passes every gate, so there is no recommendation.");
            return new Recommendation(null, demonstration, notes);
        }

        var cheapest = eligible.Min(c => c.CostPerClaim!.Value);
        var tied = eligible.Where(c => c.CostPerClaim!.Value - cheapest <= CostTieGbp).ToList();
        // A p95 within the latency tie band of the best counts as equal, so the next key decides.
        var bestP95 = tied.Min(c => c.P95Ms ?? double.MaxValue);
        var p95Tied = tied.Where(c => (c.P95Ms ?? double.MaxValue) - bestP95 <= LatencyTieMs).ToList();
        var chosen = p95Tied
            .OrderBy(c => c.Summary!.Model?.MalformedOutputRate ?? 0)
            .ThenBy(c => c.Label, StringComparer.Ordinal)
            .First();

        notes.Add(string.Empty);
        notes.Add(tied.Count > 1
            ? $"**Pick: `{chosen.Label}`.** {tied.Count} passing candidates tied on cost (within £0.0001 per claim); the tie was broken by p95 latency, then malformed-output rate, then label."
            : $"**Pick: `{chosen.Label}`.** It is the cheapest of the {eligible.Count} candidate(s) that pass every gate, at £{Money(chosen.CostPerClaim)} per claim (£{Money(chosen.Summary!.Model?.CostPer1000ClaimsGbp)} per 1,000).");
        if (chosen.PriceUnverified)
        {
            notes.Add("Caveat: the price used for this model is marked verify-before-use in the registry. Confirm it on the provider's page before relying on the cost ranking.");
        }

        if (demonstration)
        {
            notes.Add("This pick is a demonstration on FAKE rows. It is not a model recommendation.");
        }

        return new Recommendation(chosen.Label, demonstration, notes);
    }

    internal static string Money(double? v) => v is { } x ? x.ToString("0.0000", CultureInfo.InvariantCulture) : "n/a";

    internal static string Ms(double? v) => v is { } x ? x.ToString("0", CultureInfo.InvariantCulture) : "n/a";
}

/// <summary>Writes <c>comparison.md</c> and <c>comparison.json</c>.</summary>
public static class ComparisonReport
{
    public const string FakeLabel = "FAKE — pipeline demonstration, not a model result";
    public const string NotRunLabel = "not yet run against real models";

    private static string P(double? v) => v is { } x ? x.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";

    private static string Pct(double? v) => v is { } x ? (x * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "n/a";

    public static string ToMarkdown(string runId, string split, DateTimeOffset generated, IReadOnlyList<CompareRow> rows, Recommendation recommendation, Thresholds? gates, int parallelism)
    {
        var sb = new StringBuilder();
        var realRan = rows.Any(r => r.Kind == RowKind.Model && r.Summary is not null);
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Model comparison: {split} split, run {runId}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated {generated:yyyy-MM-dd HH:mm} UTC by `compare`. Every row ran over the same {split} split of the synthetic dataset.");
        sb.AppendLine();
        if (!realRan)
        {
            sb.AppendLine("> **Not yet run against real models.** No Azure AI Foundry deployment was available, so no model has been measured. "
                + "The baseline row is a real run of the local heuristic extractor and rules assessor. Rows marked \"" + NotRunLabel + "\" have no numbers on purpose. "
                + "Nothing here is an accuracy, cost or latency figure for a real model. See docs/runbook-model-comparison.md to run it.");
            sb.AppendLine();
        }

        if (rows.Any(r => r.Kind == RowKind.Fake))
        {
            sb.AppendLine("> **Rows labelled FAKE are scripted stand-ins** that know the ground truth (the oracle) or add scripted faults (the noisy one). "
                + "Their tokens, prices and latencies are invented. They show that token accounting, cost maths, malformed-output counts and the decision rules work end to end. They say nothing about any real model.");
            sb.AppendLine();
        }

        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Config | Prompt | Field F1 | Route acc. | Citations exist / text | Guard removed | Malformed | p50 ms | p95 ms | £ / claim | £ / 1,000 | Gates |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            sb.AppendLine(Row(r));
        }

        sb.AppendLine();
        sb.AppendLine("- **Guard removed**: citations and findings the citation guard dropped because the wording search never returned that clause. Counts, summed over the split.");
        sb.AppendLine("- **Malformed**: model replies that were empty, not JSON, or not the schema's shape, as a share of model calls.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Latency** is wall-clock per claim, end to end, with {parallelism} claim(s) in flight and model calls bounded by the call policy, so it includes queueing and any 429 back-off. It is not a single-request latency.");
        sb.AppendLine("- **£** is tokens reported by each call times the registry price. `*` after a figure means the price is marked verify-before-use. A model that reports no token usage shows n/a.");
        sb.AppendLine("- **Gates**: pass or fail against the gate file below. The baseline is the reference and is not gated here.");
        sb.AppendLine();

        var detail = rows.Where(r => r.Summary?.Model is not null).ToList();
        if (detail.Count > 0)
        {
            sb.AppendLine("## Model call detail");
            sb.AppendLine();
            sb.AppendLine("| Config | Calls | Input tokens | Output tokens | Rate-limit retries | Content-filter refusals | Failed calls | Pipeline errors | Malformed / schema-violating replies |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var r in detail)
            {
                var m = r.Summary!.Model!;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Name(r)} | {m.Calls} | {m.InputTokens:N0} | {m.OutputTokens:N0} | {m.RateLimitRetries} | {m.ContentFilterRefusals} | {m.FailedCalls} | {r.Summary.PipelineErrors} | {m.MalformedOutputs} / {m.SchemaViolations} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Recommendation");
        sb.AppendLine();
        sb.AppendLine("Generated by the decision rules below from the table above. It is not hand-written.");
        sb.AppendLine();
        if (recommendation.Chosen is null && !realRan && !rows.Any(r => r.Kind == RowKind.Fake && r.Summary is not null))
        {
            sb.AppendLine("**No recommendation.** Not yet run against real models, so there is no data to choose from. The baseline is not a candidate.");
        }
        else
        {
            foreach (var line in recommendation.Explanation)
            {
                sb.AppendLine(line);
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Decision rules");
        sb.AppendLine();
        var n = 1;
        foreach (var rule in Recommender.Rules)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{n++}. {rule}");
        }

        sb.AppendLine();
        if (gates is not null)
        {
            sb.AppendLine("## Gate thresholds used");
            sb.AppendLine();
            sb.AppendLine("| Metric | Required |");
            sb.AppendLine("|---|---|");
            foreach (var (name, bound) in gates.Metrics.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| `{name}` | {Gate.Describe(bound)} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Limits");
        sb.AppendLine();
        sb.AppendLine("Synthetic data written by one author, 36 held-out cases: a difference of one case is about 3 points of route accuracy. Treat close rows as ties in quality and let the cost rule decide, "
            + "and re-run before relying on a pick. See evals/README.md.");
        return sb.ToString();
    }

    private static string Name(CompareRow r) => r.Kind switch
    {
        RowKind.Fake => $"**{FakeLabel}**: `{r.Label}`",
        _ => $"`{r.Label}`",
    };

    private static string Row(CompareRow r)
    {
        if (r.Summary is null)
        {
            return $"| `{r.Label}` ({r.Model}) | {r.Prompt} | {NotRunLabel}{(r.Reason is null ? "" : ": " + r.Reason)} | | | | | | | | | |";
        }

        var s = r.Summary;
        var m = s.Model;
        var star = r.PriceUnverified ? "*" : "";
        var gate = r.Kind == RowKind.Baseline ? "reference" : r.Gate is null ? "n/a" : r.Gate.Passed ? "pass" : $"FAIL ({r.Gate.Failures.Count()})";
        var prompt = s.Run?.Prompts.Count > 0 ? string.Join(", ", s.Run.Prompts.Select(p => $"{p.Version} `{p.Sha256[..8]}`")) : "none";
        return string.Create(CultureInfo.InvariantCulture,
            $"| {Name(r)} | {prompt} | {P(s.FieldsOverall.F1)} | {Pct(s.Route.Accuracy)} | {Pct(s.Groundedness.ExistsShare)} / {Pct(s.Groundedness.TextMatchShare)} | {(m is null ? "0" : m.GuardRemovals)} | {(m is null ? "n/a" : Pct(m.MalformedOutputRate))} | {Recommender.Ms(r.P50Ms)} | {Recommender.Ms(r.P95Ms)} | {(m is null ? "0.0000" : Recommender.Money(r.CostPerClaim))}{star} | {(m is null ? "0.00" : m.CostPer1000ClaimsGbp?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a")}{star} | {gate} |");
    }

    public static string ToJson(string runId, string split, IReadOnlyList<CompareRow> rows, Recommendation recommendation) =>
        JsonSerializer.Serialize(new
        {
            runId,
            split,
            notYetRunAgainstRealModels = !rows.Any(r => r.Kind == RowKind.Model && r.Summary is not null),
            rows = rows.Select(r => new
            {
                r.Label, kind = r.Kind.ToString(), r.Model, r.Prompt, r.Reason, r.PriceUnverified, gatesPassed = r.Gate?.Passed,
                metrics = r.Summary?.Metrics, run = r.Summary?.Run, usage = r.Summary?.Model,
            }),
            recommendation,
        }, Report.JsonOptions) + "\n";
}
