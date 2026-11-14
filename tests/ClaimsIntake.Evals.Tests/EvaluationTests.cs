using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Evals;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Policy;
using ClaimsIntake.Pipeline.Text;

namespace ClaimsIntake.Evals.Tests;

internal static class Repo
{
    public static string Dataset => DatasetLoader.FindDefaultFolder(AppContext.BaseDirectory);

    public static IReadOnlyList<PolicyWording> Wordings { get; } =
        PolicyWordingParser.LoadFolder(Path.Combine(AppContext.BaseDirectory, "policy-wordings"));

    public static EvalCase Case(
        string id = "t1", string route = "StraightToHandler", string[]? issues = null, ExpectedFinding[]? findings = null,
        ExpectedFields? fields = null) =>
        new()
        {
            Id = id, File = id + ".txt", Split = "dev", Tags = ["clean"],
            Context = new CaseContext(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero), [], null, null),
            Expected = new Expected(
                fields ?? new ExpectedFields("TM-CP-100001", "Brindle & Marsh Joinery Ltd", "2026-11-03", "water", 12500m, "2026-11-06"),
                issues ?? [], findings ?? [new ExpectedFinding("CoverageClause", "3.2")], route, "document"),
        };

    public static ProposedClaim Claim(
        string policy = "TM-CP-100001", string name = "Brindle and Marsh Joinery Limited", string loss = "2026-11-03",
        string description = "A water pipe burst in the store room.", string amount = "£12,500", double confidence = 0.95,
        IReadOnlyList<CoverageFinding>? findings = null, ReviewRoute route = ReviewRoute.StraightToHandler,
        IReadOnlyList<ValidationIssue>? issues = null, DateOnly? notified = null)
    {
        var clause = Wordings.First(w => w.Id == "TM-PW-CC-2026").Clauses.First(c => c.ClauseRef == "3.2");
        findings ??= [new CoverageFinding(FindingKind.CoverageClause, "covered", [new ClauseCitation(clause.WordingId, clause.ClauseRef, clause.Heading, clause.Text)])];
        return new ProposedClaim("inbox/t1.txt", DateTimeOffset.UnixEpoch,
            [
                new ExtractedField(ClaimFields.PolicyNumber, policy, confidence), new ExtractedField(ClaimFields.ClaimantName, name, confidence),
                new ExtractedField(ClaimFields.LossDate, loss, confidence), new ExtractedField(ClaimFields.LossDescription, description, confidence),
                new ExtractedField(ClaimFields.EstimatedAmount, amount, confidence),
            ],
            issues ?? [], ["text"], new ClaimAssessment("rules", findings, route, "r", []), null, notified ?? new DateOnly(2026, 11, 6), "document");
    }

    public static CaseScore Score(EvalCase item, ProposedClaim? claim) =>
        CaseScorer.Score(new CaseRun(item, claim, [], null), Wordings);
}

public class CaseScorerTests
{
    [Fact]
    public void A_correct_claim_scores_perfectly_after_normalisation()
    {
        var score = Repo.Score(Repo.Case(), Repo.Claim());

        Assert.All(score.Fields.Values, o => Assert.Equal(FieldOutcome.Correct, o));
        Assert.Equal(new Counts(6, 0, 0), score.FieldCounts());
        Assert.Equal(new Counts(1, 0, 0), score.FindingCounts);
        Assert.True(score.RouteCorrect);
        Assert.True(score.PredictedAutoRoutable && score.ExpectedAutoRoutable);
        Assert.DoesNotContain(score.Citations, c => !c.Exists || !c.TextMatches);
    }

    [Fact]
    public void A_wrong_amount_and_a_missing_loss_date_are_counted_strictly()
    {
        var score = Repo.Score(Repo.Case(), Repo.Claim(amount: "£12,5", loss: ""));

        Assert.Equal(FieldOutcome.Wrong, score.Fields[FieldNames.EstimatedAmount]);
        Assert.Equal(FieldOutcome.Missed, score.Fields[FieldNames.LossDate]);
        Assert.Equal(new Counts(4, 1, 2), score.FieldCounts());
    }

    [Fact]
    public void The_cause_is_the_class_of_the_extracted_description()
    {
        var score = Repo.Score(Repo.Case(), Repo.Claim(description: "There was a fire in the back office."));

        Assert.Equal(FieldOutcome.Wrong, score.Fields[FieldNames.Cause]);
        Assert.Equal("fire", score.PredictedFields[FieldNames.Cause]);
    }

    [Fact]
    public void A_policy_number_that_differs_in_case_is_wrong()
    {
        var score = Repo.Score(Repo.Case(), Repo.Claim(policy: "tm-cp-100001"));

        Assert.Equal(FieldOutcome.Wrong, score.Fields[FieldNames.PolicyNumber]);
    }

    [Fact]
    public void Low_confidence_warnings_are_reported_but_not_scored_as_issues()
    {
        var issue = new ValidationIssue(IssueCode.LowConfidence, IssueSeverity.Warning, "loss_date", "low");

        var score = Repo.Score(Repo.Case(), Repo.Claim(issues: [issue]));

        Assert.True(score.HadLowConfidenceWarning);
        Assert.Empty(score.PredictedIssues);
        Assert.Equal(Counts.Zero, score.IssueCounts);
        Assert.False(score.PassedWithNoFlags); // flagged, so not a silent pass
    }

    [Fact]
    public void An_unexpected_blocking_issue_is_a_false_positive_and_stops_auto_routing()
    {
        var issue = new ValidationIssue(IssueCode.UnknownPolicy, IssueSeverity.Error, "policy_number", "unknown");

        var score = Repo.Score(Repo.Case(), Repo.Claim(issues: [issue]));

        Assert.Equal(new Counts(0, 1, 0), score.IssueCounts);
        Assert.False(score.PredictedAutoRoutable);
        Assert.True(score.ExpectedAutoRoutable);
    }

    [Fact]
    public void An_invalid_amount_warning_does_not_stop_auto_routing()
    {
        var issue = new ValidationIssue(IssueCode.InvalidAmount, IssueSeverity.Warning, "amount", "bad");

        Assert.True(Repo.Score(Repo.Case(), Repo.Claim(issues: [issue])).PredictedAutoRoutable);
    }

    [Fact]
    public void Findings_match_on_kind_and_clause_not_on_wording_or_text()
    {
        var wrongClause = new CoverageFinding(FindingKind.CoverageClause, "x",
            [new ClauseCitation("TM-PW-CC-2026", "3.1", "Fire", "t")]);

        var score = Repo.Score(Repo.Case(), Repo.Claim(findings: [wrongClause]));

        Assert.Equal(new Counts(0, 1, 1), score.FindingCounts);
    }

    [Fact]
    public void A_duplicate_finding_without_a_citation_matches_on_kind_alone()
    {
        var item = Repo.Case(route: "SeniorReview", findings: [new ExpectedFinding("PossibleDuplicate", "")]);
        var duplicate = new CoverageFinding(FindingKind.PossibleDuplicate, "dup", [], "LEG-1");

        var score = Repo.Score(item, Repo.Claim(findings: [duplicate], route: ReviewRoute.SeniorReview));

        Assert.Equal(new Counts(1, 0, 0), score.FindingCounts);
        Assert.True(score.RouteCorrect);
    }

    [Fact]
    public void A_citation_to_a_clause_that_does_not_exist_or_with_altered_text_is_caught()
    {
        var real = Repo.Wordings.First(w => w.Id == "TM-PW-CC-2026").Clauses.First(c => c.ClauseRef == "3.2");
        CoverageFinding Finding(string wording, string clause, string text) =>
            new(FindingKind.CoverageClause, "x", [new ClauseCitation(wording, clause, "h", text)]);

        var checks = CaseScorer.CheckCitations(
            [Finding(real.WordingId, real.ClauseRef, "  " + real.Text.Replace(" ", "  ") + " "), // whitespace differences are fine
             Finding(real.WordingId, "9.9", "invented"),
             Finding(real.WordingId, real.ClauseRef, "We will pay for anything."),
             Finding("TM-PW-NOPE", "3.2", real.Text)],
            Repo.Wordings);

        Assert.Equal([(true, true), (false, false), (true, false), (false, false)], checks.Select(c => (c.Exists, c.TextMatches)));
    }

    [Fact]
    public void A_claim_with_no_flags_and_a_wrong_critical_field_is_a_silent_error()
    {
        var score = Repo.Score(Repo.Case(), Repo.Claim(amount: "£9,999"));

        Assert.True(score.PassedWithNoFlags);
        Assert.True(score.WrongCriticalField);
    }

    [Fact]
    public void A_pipeline_that_threw_scores_as_nothing_found_and_no_route()
    {
        var score = CaseScorer.Score(new CaseRun(Repo.Case(), null, [], "InvalidOperationException"), Repo.Wordings);

        Assert.Equal(CaseScorer.NoRoute, score.PredictedRoute);
        Assert.False(score.PassedWithNoFlags);
        Assert.Equal(new Counts(0, 0, 6), score.FieldCounts());
        Assert.Equal("InvalidOperationException", score.Error);
    }
}

public class SummaryTests
{
    [Fact]
    public void The_summary_micro_averages_and_breaks_down_by_tag()
    {
        var good = Repo.Score(Repo.Case("a"), Repo.Claim());
        var bad = Repo.Score(Repo.Case("b"), Repo.Claim(policy: "TM-CP-100002", route: ReviewRoute.SeniorReview));

        var summary = Summariser.Summarise("r", "dev", "heuristic", "rules", [good, bad]);

        Assert.Equal(2, summary.Cases);
        Assert.Equal(new Counts(11, 1, 1).F1, summary.FieldsOverall.F1);
        Assert.Equal(0.5, summary.Route.Accuracy);
        Assert.Equal(0.5, summary.Metrics["route_accuracy"]);
        Assert.Equal(0.5, summary.AutoRoute.PredictedAutoRoutableRate);
        Assert.Equal(1.0, summary.Groundedness.ExistsShare);
        var tag = Assert.Single(summary.Tags);
        Assert.Equal((("clean"), 2), (tag.Tag, tag.Cases));
        Assert.Equal(0.0, summary.CostPerClaimGbp);
    }

    [Fact]
    public void An_empty_run_has_no_rates()
    {
        var summary = Summariser.Summarise("r", "dev", "heuristic", "rules", []);

        Assert.Null(summary.Route.Accuracy);
        Assert.Null(summary.FieldsOverall.F1);
        Assert.Null(summary.Groundedness.ExistsShare);
    }

    [Fact]
    public void Latency_percentiles_come_from_the_stage_timings()
    {
        var item = Repo.Case();
        var runs = Enumerable.Range(1, 20).Select(i => CaseScorer.Score(
            new CaseRun(item, Repo.Claim(), [new StageTiming("process", i)], null), Repo.Wordings)).ToList();

        var latency = Summariser.Summarise("r", "dev", "h", "r", runs).Latency.Single();

        Assert.Equal((10.0, 19.0), (latency.P50Ms, latency.P95Ms));
    }
}
