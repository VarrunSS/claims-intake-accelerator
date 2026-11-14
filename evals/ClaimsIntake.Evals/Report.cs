using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsIntake.Domain.Assessment;

namespace ClaimsIntake.Evals;

/// <summary>Writes <c>report.md</c> (for people and the CI step summary) and <c>results.json</c> (for machines).</summary>
public static class Report
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(EvalSummary summary, IReadOnlyList<CaseScore> scores, GateResult? gate) =>
        JsonSerializer.Serialize(new { summary, gate = gate?.Checks, cases = scores }, JsonOptions) + "\n";

    private static string P(double? v) => v is { } x ? x.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";

    private static string Pct(double? v) => v is { } x ? (x * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "n/a";

    private static string Ms(double? v) => v is { } x ? x.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

    /// <param name="scores">Per-case scores.</param>
    /// <param name="showMisses">List what was wrong in each case. Done for the dev split only: the test split is not for tuning against.</param>
    public static string ToMarkdown(EvalSummary s, IReadOnlyList<CaseScore> scores, GateResult? gate, bool showMisses)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Evaluation report: {s.Split} split, run {s.RunId}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"{s.Cases} synthetic claims, extractor `{s.Extractor}`, assessor `{s.Assessor}`. Fully offline: no model, no network, no credentials. **These are not real-model numbers**; the model-backed paths are compared in Milestone 5.");
        sb.AppendLine();
        sb.AppendLine(
            "The data is synthetic and written by the same author as the extractor, so the numbers show where the heuristics are weak, not how a real "
            + "insurer's mail would score. See evals/README.md.");
        sb.AppendLine();

        if (gate is not null)
        {
            sb.AppendLine(gate.Passed ? "## Gate: PASSED" : "## Gate: FAILED");
            sb.AppendLine();
            sb.AppendLine("| Metric | Required | Measured | Result |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var c in gate.Checks)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| `{c.Metric}` | {c.Requirement} | {P(c.Actual)} | {(c.Passed ? "pass" : "FAIL: " + c.Reason)} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Headline");
        sb.AppendLine();
        sb.AppendLine("| Measure | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Field F1, overall (micro, six fields) | {P(s.FieldsOverall.F1)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Validation issues: precision / recall | {P(s.Issues.Precision)} / {P(s.Issues.Recall)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Findings (kind + clause): precision / recall | {P(s.Findings.Precision)} / {P(s.Findings.Recall)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Route accuracy | {Pct(s.Route.Accuracy)} ({s.Route.Matrix.Sum(r => r.Value.Where(c => c.Key == r.Key).Sum(c => c.Value))} of {s.Route.Total}) |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Citation groundedness (clause exists / text matches) | {Pct(s.Groundedness.ExistsShare)} / {Pct(s.Groundedness.TextMatchShare)} of {s.Groundedness.Citations} citations |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Auto-routable | {Pct(s.AutoRoute.PredictedAutoRoutableRate)} (truth {Pct(s.AutoRoute.ExpectedAutoRoutableRate)}) |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Needs a person first | {Pct(s.AutoRoute.PredictedNeedsHumanRate)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Cost per claim | £{s.CostPerClaimGbp:0.00} (local, no model; measured in Milestone 5) |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Pipeline errors (threw instead of producing a claim) | {s.PipelineErrors} |");
        sb.AppendLine();

        sb.AppendLine("## Fields");
        sb.AppendLine();
        sb.AppendLine("Strict scoring: a wrong value counts as one false positive and one false negative. A field absent from both the document and the output scores nothing.");
        sb.AppendLine();
        sb.AppendLine("| Field | TP | FP | FN | Precision | Recall | F1 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var (name, f) in s.Fields)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {f.TruePositives} | {f.FalsePositives} | {f.FalseNegatives} | {P(f.Precision)} | {P(f.Recall)} | {P(f.F1)} |");
        }

        var o = s.FieldsOverall;
        sb.AppendLine(CultureInfo.InvariantCulture, $"| **overall (micro)** | {o.TruePositives} | {o.FalsePositives} | {o.FalseNegatives} | {P(o.Precision)} | {P(o.Recall)} | {P(o.F1)} |");
        sb.AppendLine();

        sb.AppendLine("## Validation issues and findings");
        sb.AppendLine();
        sb.AppendLine("Issues exclude `LowConfidence`, which is the extractor's own doubt rather than a fact about the document. "
            + $"{Pct(s.LowConfidenceShare)} of claims carried at least one low-confidence warning.");
        sb.AppendLine();
        sb.AppendLine("| | TP | FP | FN | Precision | Recall | F1 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var (label, f) in new[] { ("Validation issue codes", s.Issues), ("Findings (kind + clause)", s.Findings) })
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {f.TruePositives} | {f.FalsePositives} | {f.FalseNegatives} | {P(f.Precision)} | {P(f.Recall)} | {P(f.F1)} |");
        }

        sb.AppendLine();

        sb.AppendLine("## Route");
        sb.AppendLine();
        sb.AppendLine("Rows are the expected route, columns the route the pipeline chose.");
        sb.AppendLine();
        sb.AppendLine("| expected \\ predicted | " + string.Join(" | ", s.Route.Labels) + " |");
        sb.AppendLine("|---|" + string.Join("|", s.Route.Labels.Select(_ => "---")) + "|");
        foreach (var e in s.Route.Labels)
        {
            sb.AppendLine("| " + e + " | " + string.Join(" | ", s.Route.Labels.Select(p => s.Route.Matrix[e][p].ToString(CultureInfo.InvariantCulture))) + " |");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"SeniorReview precision {P(s.Route.SeniorReviewPrecision)}, recall {P(s.Route.SeniorReviewRecall)}. "
            + $"StraightToHandler precision {P(s.Route.StraightToHandlerPrecision)}, recall {P(s.Route.StraightToHandlerRecall)}.");
        sb.AppendLine();

        sb.AppendLine("## Citation groundedness");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"{s.Groundedness.Citations} clauses were cited. {s.Groundedness.Existing} exist in the policy wordings and {s.Groundedness.TextMatching} quote the wording's text exactly. For the rules assessor this is 100% by construction: it can only cite a clause that `search_policy_wording` returned in the same run, and the text it shows is the wording's. The number becomes informative for the agent assessor in Milestone 5, where the citation guard is what keeps it at 100%.");
        sb.AppendLine();

        var a = s.AutoRoute;
        sb.AppendLine("## Auto-routable versus needs a person");
        sb.AppendLine();
        sb.AppendLine("A claim is *auto-routable* when the route is StraightToHandler and no blocking validation issue was raised: a handler can check and approve it. "
            + "It ties to measure M3 in docs/01-success-metrics.md. The rest need a person first, and are the system correctly declining to pass them.");
        sb.AppendLine();
        sb.AppendLine("| | Pipeline | Ground truth |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Auto-routable | {Pct(a.PredictedAutoRoutableRate)} | {Pct(a.ExpectedAutoRoutableRate)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| No data repair needed (M3: no blocking validation error) | {Pct(a.PredictedNoDataRepairRate)} | {Pct(a.ExpectedNoDataRepairRate)} |");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Of the claims the pipeline auto-routed, {Pct(a.AutoRoutePrecision)} should have been (precision); {a.WronglyAutoRouted} were auto-routed that needed a person. "
            + $"Of the claims that should be auto-routable, {Pct(a.AutoRouteRecall)} were (recall).");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Guardrail G2, silent errors: {a.PassedWithNoFlags} claims raised no issue at all, and {a.PassedWithNoFlagsButWrongCriticalField} of them had a wrong or missing critical field ({Pct(a.SilentErrorRate)}). The target is 1% or fewer.");
        sb.AppendLine();

        sb.AppendLine("## Latency per stage");
        sb.AppendLine();
        sb.AppendLine("Milliseconds, from the pipeline's own spans, in process, on the machine that ran the evaluation (one warm-up claim discarded). "
            + "`process` is the whole claim. The numbers vary a little from run to run and are not gated tightly.");
        sb.AppendLine();
        sb.AppendLine("| Stage | n | p50 | p95 | mean | cost per claim |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var l in s.Latency)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {l.Stage} | {l.Samples} | {Ms(l.P50Ms)} | {Ms(l.P95Ms)} | {Ms(l.MeanMs)} | £0.00 |");
        }

        sb.AppendLine();

        sb.AppendLine("## By tag");
        sb.AppendLine();
        sb.AppendLine("A case can carry several tags, so the rows overlap. Small groups are noisy: read the count first. n/a means the group has nothing to score for that measure.");
        sb.AppendLine();
        sb.AppendLine("| Tag | Cases | Field F1 | Issue F1 | Finding F1 | Route accuracy |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var t in s.Tags.OrderBy(t => t.FieldF1 ?? 2).ThenBy(t => t.Tag, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {t.Tag} | {t.Cases} | {P(t.FieldF1)} | {P(t.IssueF1)} | {P(t.FindingF1)} | {Pct(t.RouteAccuracy)} |");
        }

        sb.AppendLine();

        if (showMisses)
        {
            sb.AppendLine("## Misses (dev split)");
            sb.AppendLine();
            sb.AppendLine("What was wrong in each case that was not fully right. Only the dev split is listed: it is the one to look at while improving the extractor.");
            sb.AppendLine();
            foreach (var c in scores)
            {
                var problems = Misses(c).ToList();
                if (problems.Count > 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- `{c.Id}` ({string.Join(", ", c.Tags)}): {string.Join("; ", problems)}");
                }
            }

            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Misses");
            sb.AppendLine();
            sb.AppendLine("Not listed for this split, by design: the test split is held out and is not used to tune anything. Per-case scores are in `results.json` for audit.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>A one-line-per-problem description of what went wrong in a case, for the dev report.</summary>
    public static IEnumerable<string> Misses(CaseScore c)
    {
        foreach (var (field, outcome) in c.Fields.Where(f => f.Value is not (FieldOutcome.Correct or FieldOutcome.BothAbsent)))
        {
            yield return $"{field} {outcome.ToString().ToLowerInvariant()} (got {(c.PredictedFields[field] is { } v ? $"\"{v}\"" : "nothing")})";
        }

        var missedIssues = c.ExpectedIssues.Except(c.PredictedIssues).ToList();
        var extraIssues = c.PredictedIssues.Except(c.ExpectedIssues).ToList();
        if (missedIssues.Count > 0)
        {
            yield return $"issues missed: {string.Join(", ", missedIssues)}";
        }

        if (extraIssues.Count > 0)
        {
            yield return $"issues unexpected: {string.Join(", ", extraIssues)}";
        }

        var missedFindings = c.ExpectedFindings.Except(c.PredictedFindings).ToList();
        var extraFindings = c.PredictedFindings.Except(c.ExpectedFindings).ToList();
        if (missedFindings.Count > 0)
        {
            yield return $"findings missed: {string.Join(", ", missedFindings)}";
        }

        if (extraFindings.Count > 0)
        {
            yield return $"findings unexpected: {string.Join(", ", extraFindings)}";
        }

        if (!c.RouteCorrect)
        {
            yield return $"route {c.PredictedRoute}, expected {c.ExpectedRoute}";
        }

        if (c.Error is not null)
        {
            yield return $"the pipeline threw {c.Error}";
        }
    }

    /// <summary>The short text printed to the terminal.</summary>
    public static string ToSummaryText(EvalSummary s, GateResult? gate)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Evaluation: {s.Split} split, {s.Cases} cases, extractor {s.Extractor}, assessor {s.Assessor}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  field F1 (micro)      {P(s.FieldsOverall.F1)}   precision {P(s.FieldsOverall.Precision)}   recall {P(s.FieldsOverall.Recall)}");
        foreach (var (name, f) in s.Fields)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {name,-22} F1 {P(f.F1)}   P {P(f.Precision)}   R {P(f.Recall)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"  issues                P {P(s.Issues.Precision)}   R {P(s.Issues.Recall)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  findings              P {P(s.Findings.Precision)}   R {P(s.Findings.Recall)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  route accuracy        {Pct(s.Route.Accuracy)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  citation groundedness {Pct(s.Groundedness.ExistsShare)} exist, {Pct(s.Groundedness.TextMatchShare)} text match ({s.Groundedness.Citations} citations)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  auto-routable         {Pct(s.AutoRoute.PredictedAutoRoutableRate)} (truth {Pct(s.AutoRoute.ExpectedAutoRoutableRate)}); needs a person {Pct(s.AutoRoute.PredictedNeedsHumanRate)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  silent errors (G2)    {Pct(s.AutoRoute.SilentErrorRate)} ({s.AutoRoute.PassedWithNoFlagsButWrongCriticalField} of {s.AutoRoute.PassedWithNoFlags} unflagged claims)");
        var total = s.Latency.FirstOrDefault(l => l.Stage == Summariser.Total);
        sb.AppendLine(CultureInfo.InvariantCulture, $"  latency per claim     p50 {Ms(total?.P50Ms)} ms, p95 {Ms(total?.P95Ms)} ms; cost £0.00 (local)");
        if (gate is not null)
        {
            sb.AppendLine(gate.Passed
                ? $"  gate: PASSED ({gate.Checks.Count} thresholds)"
                : $"  gate: FAILED ({gate.Failures.Count()} of {gate.Checks.Count} thresholds)");
            foreach (var f in gate.Failures)
            {
                sb.AppendLine($"    FAIL {f.Metric} (required {f.Requirement}): {f.Reason}");
            }
        }

        return sb.ToString();
    }
}
