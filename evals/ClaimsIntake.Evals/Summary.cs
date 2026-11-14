using ClaimsIntake.Domain.Assessment;

namespace ClaimsIntake.Evals;

public sealed record PrfSummary(int TruePositives, int FalsePositives, int FalseNegatives, double? Precision, double? Recall, double? F1)
{
    public static PrfSummary From(Counts c) => new(c.TruePositives, c.FalsePositives, c.FalseNegatives, c.Precision, c.Recall, c.F1);
}

public sealed record RouteSummary(
    IReadOnlyList<string> Labels, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Matrix, int Total, double? Accuracy,
    double? SeniorReviewPrecision, double? SeniorReviewRecall, double? StraightToHandlerPrecision, double? StraightToHandlerRecall);

public sealed record GroundednessSummary(int Citations, int Existing, int TextMatching, double? ExistsShare, double? TextMatchShare);

/// <summary>"Auto-routable": the pipeline says StraightToHandler and raised no blocking issue. Everything else needs a person first.</summary>
public sealed record AutoRouteSummary(
    int Cases, double? PredictedAutoRoutableRate, double? ExpectedAutoRoutableRate, double? PredictedNeedsHumanRate,
    double? AutoRoutePrecision, double? AutoRouteRecall, int WronglyAutoRouted,
    double? PredictedNoDataRepairRate, double? ExpectedNoDataRepairRate,
    int PassedWithNoFlags, int PassedWithNoFlagsButWrongCriticalField, double? SilentErrorRate);

public sealed record LatencySummary(string Stage, int Samples, double? P50Ms, double? P95Ms, double? MeanMs);

public sealed record TagSummary(string Tag, int Cases, double? FieldF1, double? IssueF1, double? FindingF1, double? RouteAccuracy);

/// <summary>Everything a run reports, ready to write as results.json or as report.md.</summary>
public sealed record EvalSummary(
    string RunId,
    string Split,
    string Extractor,
    string Assessor,
    int Cases,
    int PipelineErrors,
    IReadOnlyDictionary<string, PrfSummary> Fields,
    PrfSummary FieldsOverall,
    PrfSummary Issues,
    double? LowConfidenceShare,
    PrfSummary Findings,
    RouteSummary Route,
    GroundednessSummary Groundedness,
    AutoRouteSummary AutoRoute,
    IReadOnlyList<LatencySummary> Latency,
    double CostPerClaimGbp,
    IReadOnlyList<TagSummary> Tags,
    IReadOnlyDictionary<string, double?> Metrics,
    RunInfo? Run = null,
    ModelUsageSummary? Model = null);

public static class Summariser
{
    public const string Total = "process";

    /// <summary>The stages in pipeline order, so the latency table reads top to bottom.</summary>
    public static readonly IReadOnlyList<string> StageOrder =
        ["input_quality", "extract_text", "input_quality_text", "extract_fields", "lookup_policy", "validate", "assess", "handoff", "process"];

    private static readonly string[] RouteLabels = [nameof(ReviewRoute.StraightToHandler), nameof(ReviewRoute.SeniorReview), CaseScorer.NoRoute];

    public static EvalSummary Summarise(string runId, string split, string extractor, string assessor, IReadOnlyList<CaseScore> scores, RunInfo? run = null)
    {
        var perField = FieldNames.All.ToDictionary(f => f, f => PrfSummary.From(Counts.Sum(scores.Select(s => s.FieldCounts(f)))));
        var overall = Counts.Sum(scores.Select(s => s.FieldCounts()));
        var issues = Counts.Sum(scores.Select(s => s.IssueCounts));
        var findings = Counts.Sum(scores.Select(s => s.FindingCounts));

        var matrix = new ConfusionMatrix(RouteLabels);
        foreach (var s in scores)
        {
            matrix.Add(s.ExpectedRoute, s.PredictedRoute);
        }

        var citations = scores.SelectMany(s => s.Citations).ToList();
        var existing = citations.Count(c => c.Exists);
        var matching = citations.Count(c => c.TextMatches);
        var grounded = new GroundednessSummary(citations.Count, existing, matching,
            citations.Count == 0 ? null : existing / (double)citations.Count,
            citations.Count == 0 ? null : matching / (double)citations.Count);

        var summary = new EvalSummary(
            runId, split, extractor, assessor, scores.Count, scores.Count(s => s.Error is not null),
            perField, PrfSummary.From(overall), PrfSummary.From(issues),
            scores.Count == 0 ? null : scores.Count(s => s.HadLowConfidenceWarning) / (double)scores.Count,
            PrfSummary.From(findings), RouteOf(matrix), grounded, AutoRouteOf(scores), LatencyOf(scores), 0.0, TagsOf(scores),
            new Dictionary<string, double?>(), run);
        var model = run is { IsModelRun: true } ? ModelUsageOf(scores, run) : null;
        summary = summary with { Model = model, CostPerClaimGbp = model?.CostPerClaimGbp ?? 0.0 };
        return summary with { Metrics = FlatMetrics(summary) };
    }

    private static ModelUsageSummary ModelUsageOf(IReadOnlyList<CaseScore> scores, RunInfo run)
    {
        var usage = scores.Select(s => s.Usage ?? UsageRecord.None).ToList();
        var input = usage.Sum(u => u.InputTokens);
        var output = usage.Sum(u => u.OutputTokens);
        var calls = usage.Sum(u => u.Calls);
        var missing = usage.Any(u => u.UsageMissing);
        var malformed = usage.Sum(u => u.MalformedOutputs);
        var schema = usage.Sum(u => u.SchemaViolations);
        var callMs = usage.SelectMany(u => u.CallMs).ToList();
        double? perClaim = run.Price is null || missing || scores.Count == 0 ? null : (double)run.Price.Cost(input, output) / scores.Count;
        return new ModelUsageSummary(
            calls, input, output, missing, perClaim, perClaim * 1000, run.Price?.IsVerified ?? false,
            usage.Sum(u => u.RateLimitRetries), usage.Sum(u => u.ContentFilterRefusals), usage.Sum(u => u.FailedCalls),
            malformed, schema, calls == 0 ? null : (malformed + schema) / (double)calls, usage.Sum(u => u.GuardRemovals),
            callMs.Count == 0 ? null : Scoring.Percentile(callMs, 50), callMs.Count == 0 ? null : Scoring.Percentile(callMs, 95));
    }

    private static RouteSummary RouteOf(ConfusionMatrix m)
    {
        var matrix = m.Labels.ToDictionary(e => e, e => (IReadOnlyDictionary<string, int>)m.Labels.ToDictionary(p => p, p => m.Get(e, p)));
        return new RouteSummary(m.Labels, matrix, m.Total, m.Accuracy,
            m.PrecisionOf(nameof(ReviewRoute.SeniorReview)), m.RecallOf(nameof(ReviewRoute.SeniorReview)),
            m.PrecisionOf(nameof(ReviewRoute.StraightToHandler)), m.RecallOf(nameof(ReviewRoute.StraightToHandler)));
    }

    private static AutoRouteSummary AutoRouteOf(IReadOnlyList<CaseScore> scores)
    {
        double? Rate(int count) => scores.Count == 0 ? null : count / (double)scores.Count;
        var predicted = scores.Count(s => s.PredictedAutoRoutable);
        var expected = scores.Count(s => s.ExpectedAutoRoutable);
        var both = scores.Count(s => s.PredictedAutoRoutable && s.ExpectedAutoRoutable);
        var passed = scores.Count(s => s.PassedWithNoFlags);
        var passedWrong = scores.Count(s => s.WrongCriticalField);
        return new AutoRouteSummary(
            scores.Count, Rate(predicted), Rate(expected), Rate(scores.Count - predicted),
            predicted == 0 ? null : both / (double)predicted, expected == 0 ? null : both / (double)expected, predicted - both,
            Rate(scores.Count(s => s.PredictedNoDataRepair)), Rate(scores.Count(s => s.ExpectedNoDataRepair)),
            passed, passedWrong, passed == 0 ? null : passedWrong / (double)passed);
    }

    private static List<LatencySummary> LatencyOf(IReadOnlyList<CaseScore> scores)
    {
        var byStage = scores.SelectMany(s => s.Timings).GroupBy(t => t.Stage).ToDictionary(g => g.Key, g => g.Select(t => t.Milliseconds).ToList());
        return [.. StageOrder.Concat(byStage.Keys.Except(StageOrder).Order()).Where(byStage.ContainsKey).Select(stage =>
        {
            var v = byStage[stage];
            return new LatencySummary(stage, v.Count, Scoring.Percentile(v, 50), Scoring.Percentile(v, 95), v.Average());
        })];
    }

    private static List<TagSummary> TagsOf(IReadOnlyList<CaseScore> scores) =>
    [
        .. scores.SelectMany(s => s.Tags).Distinct().Order(StringComparer.Ordinal).Select(tag =>
        {
            var subset = scores.Where(s => s.Tags.Contains(tag)).ToList();
            return new TagSummary(tag, subset.Count,
                Counts.Sum(subset.Select(s => s.FieldCounts())).F1,
                Counts.Sum(subset.Select(s => s.IssueCounts)).F1,
                Counts.Sum(subset.Select(s => s.FindingCounts)).F1,
                subset.Count(s => s.RouteCorrect) / (double)subset.Count);
        }),
    ];

    /// <summary>The headline numbers by name: what a threshold file can gate on.</summary>
    public static Dictionary<string, double?> FlatMetrics(EvalSummary s)
    {
        var m = new Dictionary<string, double?>
        {
            ["field_f1_overall"] = s.FieldsOverall.F1,
            ["field_precision_overall"] = s.FieldsOverall.Precision,
            ["field_recall_overall"] = s.FieldsOverall.Recall,
            ["issue_precision"] = s.Issues.Precision,
            ["issue_recall"] = s.Issues.Recall,
            ["finding_precision"] = s.Findings.Precision,
            ["finding_recall"] = s.Findings.Recall,
            ["route_accuracy"] = s.Route.Accuracy,
            ["citation_exists"] = s.Groundedness.ExistsShare,
            ["citation_text_match"] = s.Groundedness.TextMatchShare,
            ["auto_route_precision"] = s.AutoRoute.AutoRoutePrecision,
            ["silent_error_rate"] = s.AutoRoute.SilentErrorRate,
            ["pipeline_errors"] = s.PipelineErrors,
            ["cost_per_claim_gbp"] = s.Model is { UsageMissing: true } or { CostPerClaimGbp: null } ? null : s.CostPerClaimGbp,
        };
        if (s.Model is { } model)
        {
            m["cost_per_1000_claims_gbp"] = model.CostPer1000ClaimsGbp;
            m["malformed_output_rate"] = model.MalformedOutputRate;
            m["guard_removed_citations"] = model.GuardRemovals;
            m["content_filter_refusals"] = model.ContentFilterRefusals;
            m["rate_limit_retries"] = model.RateLimitRetries;
            m["model_calls"] = model.Calls;
        }

        foreach (var (field, prf) in s.Fields)
        {
            m[$"field_f1.{field}"] = prf.F1;
        }

        foreach (var l in s.Latency)
        {
            m[$"latency_p95_ms.{l.Stage}"] = l.P95Ms;
        }

        return m;
    }
}
