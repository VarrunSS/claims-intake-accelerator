using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Pipeline;
using ClaimsIntake.Pipeline.Assessment;
using ClaimsIntake.Pipeline.Policy;

namespace ClaimsIntake.Evals;

public static class FieldNames
{
    public const string PolicyNumber = "policy_number";
    public const string ClaimantName = "claimant_name";
    public const string LossDate = "loss_date";
    public const string Cause = "cause";
    public const string EstimatedAmount = "estimated_amount_gbp";
    public const string NotificationDate = "notification_date";

    public static readonly IReadOnlyList<string> All = [PolicyNumber, ClaimantName, LossDate, Cause, EstimatedAmount, NotificationDate];

    /// <summary>The fields whose wrong value, unflagged, would put a wrong claim in front of a handler (guardrail G2).</summary>
    public static readonly IReadOnlyList<string> Critical = [PolicyNumber, ClaimantName, LossDate, EstimatedAmount];
}

/// <summary>How one cited clause fared: does it exist in the corpus, and is the quoted text the wording's?</summary>
public sealed record CitationCheck(string Key, bool Exists, bool TextMatches);

/// <summary>Everything scored for one case. The raw predicted values are kept so a report can show what was wrong.</summary>
public sealed record CaseScore(
    string Id,
    IReadOnlyList<string> Tags,
    string Split,
    IReadOnlyDictionary<string, FieldOutcome> Fields,
    IReadOnlyDictionary<string, string?> PredictedFields,
    IReadOnlyList<string> ExpectedIssues,
    IReadOnlyList<string> PredictedIssues,
    bool HadLowConfidenceWarning,
    IReadOnlyList<string> ExpectedFindings,
    IReadOnlyList<string> PredictedFindings,
    IReadOnlyList<CitationCheck> Citations,
    string ExpectedRoute,
    string PredictedRoute,
    bool ExpectedAutoRoutable,
    bool PredictedAutoRoutable,
    bool ExpectedNoDataRepair,
    bool PredictedNoDataRepair,
    bool PassedWithNoFlags,
    bool WrongCriticalField,
    IReadOnlyList<StageTiming> Timings,
    string? Error)
{
    public Counts FieldCounts(string? field = null) =>
        Counts.Sum(Fields.Where(f => field is null || f.Key == field).Select(f => Scoring.ToCounts(f.Value)));

    public Counts IssueCounts => Scoring.CompareSets(ExpectedIssues, PredictedIssues);

    public Counts FindingCounts => Scoring.CompareSets(ExpectedFindings, PredictedFindings);

    public bool RouteCorrect => ExpectedRoute == PredictedRoute;
}

public static class CaseScorer
{
    public const string NoRoute = "None";

    /// <summary>Issue codes that are the extractor's own doubt rather than a fact about the document. They are reported, not scored.</summary>
    public static readonly IReadOnlySet<string> UnscoredIssues = new HashSet<string> { nameof(IssueCode.LowConfidence) };

    /// <summary>Issue codes that are warnings. Every other code blocks approval, so a claim with one needs data repair or a person.</summary>
    public static readonly IReadOnlySet<string> WarningCodes = new HashSet<string> { nameof(IssueCode.LowConfidence), nameof(IssueCode.InvalidAmount) };

    public static CaseScore Score(CaseRun run, IReadOnlyList<PolicyWording> corpus)
    {
        var item = run.Case;
        var claim = run.Claim;
        var expected = item.Expected;

        var predicted = Predict(claim);
        var outcomes = new Dictionary<string, FieldOutcome>
        {
            [FieldNames.PolicyNumber] = Scoring.Compare(Normalisation.Policy(expected.Fields.PolicyNumber), Normalisation.Policy(predicted[FieldNames.PolicyNumber])),
            [FieldNames.ClaimantName] = Scoring.Compare(Normalisation.Name(expected.Fields.ClaimantName), Normalisation.Name(predicted[FieldNames.ClaimantName])),
            [FieldNames.LossDate] = Scoring.Compare(Normalisation.Date(expected.Fields.LossDate), Normalisation.Date(predicted[FieldNames.LossDate])),
            [FieldNames.Cause] = Scoring.Compare(expected.Fields.Cause?.ToLowerInvariant(), predicted[FieldNames.Cause]?.ToLowerInvariant()),
            [FieldNames.EstimatedAmount] = Scoring.Compare(
                expected.Fields.EstimatedAmountGbp is { } e ? Normalisation.Pence(e) : null, Normalisation.Pence(predicted[FieldNames.EstimatedAmount])),
            [FieldNames.NotificationDate] = Scoring.Compare(Normalisation.Date(expected.Fields.NotificationDate), Normalisation.Date(predicted[FieldNames.NotificationDate])),
        };

        var predictedIssueCodes = claim?.Issues.Select(i => i.Code.ToString()).ToList() ?? [];
        var scoredPredicted = predictedIssueCodes.Where(c => !UnscoredIssues.Contains(c)).Distinct().Order().ToList();
        var scoredExpected = expected.IssueCodes.Where(c => !UnscoredIssues.Contains(c)).Distinct().Order().ToList();

        var findings = claim?.Assessment?.Findings ?? [];
        var predictedFindings = findings.SelectMany(f => f.Citations.Count == 0
            ? [$"{f.Kind}|"]
            : f.Citations.Select(c => $"{f.Kind}|{c.ClauseRef}")).ToList();

        var route = claim?.Assessment?.Route.ToString() ?? NoRoute;
        var predictedAuto = route == nameof(ReviewRoute.StraightToHandler) && !HasBlockingIssue(predictedIssueCodes);
        var expectedAuto = expected.Route == nameof(ReviewRoute.StraightToHandler) && !HasBlockingIssue(expected.IssueCodes);

        var flagged = predictedIssueCodes.Count > 0;
        var wrongCritical = FieldNames.Critical.Any(f => outcomes[f] is FieldOutcome.Wrong or FieldOutcome.Spurious or FieldOutcome.Missed);

        return new CaseScore(
            item.Id, item.Tags, item.Split, outcomes, predicted, scoredExpected, scoredPredicted,
            predictedIssueCodes.Contains(nameof(IssueCode.LowConfidence)),
            [.. expected.Findings.Select(f => f.Key)], predictedFindings,
            CheckCitations(findings, corpus),
            expected.Route, route, expectedAuto, predictedAuto,
            !HasBlockingIssue(expected.IssueCodes), !HasBlockingIssue(predictedIssueCodes),
            claim is not null && !flagged, claim is not null && !flagged && wrongCritical,
            run.Timings, run.Error);
    }

    private static bool HasBlockingIssue(IEnumerable<string> codes) => codes.Any(c => !WarningCodes.Contains(c));

    /// <summary>The six scored values the pipeline produced, as text, or null for each it did not find.</summary>
    public static Dictionary<string, string?> Predict(ProposedClaim? claim)
    {
        string? Field(string name) => claim?.Fields.FirstOrDefault(f => f.Name == name) is { HasValue: true } f ? f.Value!.Trim() : null;

        return new Dictionary<string, string?>
        {
            [FieldNames.PolicyNumber] = Field(ClaimFields.PolicyNumber),
            [FieldNames.ClaimantName] = Field(ClaimFields.ClaimantName),
            [FieldNames.LossDate] = Field(ClaimFields.LossDate),
            [FieldNames.Cause] = LossCauses.Classify(Field(ClaimFields.LossDescription))?.Key,
            [FieldNames.EstimatedAmount] = Field(ClaimFields.EstimatedAmount),
            [FieldNames.NotificationDate] = claim?.NotifiedOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Checks every cited clause against the corpus: does it exist, and is the quoted text the wording's text?</summary>
    public static List<CitationCheck> CheckCitations(IEnumerable<CoverageFinding> findings, IReadOnlyList<PolicyWording> corpus)
    {
        var byKey = corpus.SelectMany(w => w.Clauses).ToDictionary(c => c.Key, StringComparer.Ordinal);
        var checks = new List<CitationCheck>();
        foreach (var citation in findings.SelectMany(f => f.Citations))
        {
            var key = $"{citation.WordingId}/{citation.ClauseRef}";
            var found = byKey.TryGetValue(key, out var clause);
            checks.Add(new CitationCheck(key, found, found && Normalisation.Whitespace(clause!.Text) == Normalisation.Whitespace(citation.Text)));
        }

        return checks;
    }
}
