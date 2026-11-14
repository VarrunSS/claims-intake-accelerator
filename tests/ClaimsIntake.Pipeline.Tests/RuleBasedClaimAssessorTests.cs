using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Pipeline.Assessment;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Tools;

namespace ClaimsIntake.Pipeline.Tests;

public class RuleBasedClaimAssessorTests
{
    private static async Task<(ClaimAssessment Assessment, ClaimTools Tools)> Assess(AssessmentContext context, IExistingClaims? existing = null)
    {
        var tools = Assessing.Tools(claimId: context.ClaimId, existing: existing);
        var assessment = await new RuleBasedClaimAssessor().AssessAsync(context, tools, CancellationToken.None);
        return (assessment, tools);
    }

    private static CoverageFinding Single(ClaimAssessment a, FindingKind kind) => Assert.Single(a.Findings, f => f.Kind == kind);

    [Fact]
    public async Task A_burst_pipe_on_a_commercial_combined_policy_is_covered_and_goes_straight_to_a_handler()
    {
        var (assessment, tools) = await Assess(Assessing.Context());

        var cover = Single(assessment, FindingKind.CoverageClause);
        var citation = Assert.Single(cover.Citations);
        Assert.Equal(("TM-PW-CC-2026", "3.2", "Escape of water"), (citation.WordingId, citation.ClauseRef, citation.Heading));
        Assert.Contains("sudden and accidental escape of water", citation.Text);
        Assert.Single(assessment.Findings);
        Assert.Equal(ReviewRoute.StraightToHandler, assessment.Route);
        Assert.Equal("rules", assessment.Assessor);
        Assert.Empty(assessment.Notes);
        Assert.Equal([ToolNames.LookupPolicy, ToolNames.SearchPolicyWording, ToolNames.SearchPolicyWording, ToolNames.CheckDuplicate, ToolNames.ProposeClaim],
            tools.AuditTrail.Select(t => t.Tool));
    }

    [Fact]
    public async Task Flood_against_a_wording_that_excludes_it_cites_the_exclusion_and_needs_senior_review()
    {
        var context = Assessing.Context(policy: "TM-CP-100009", lossDate: "2026-11-10",
            description: "Heavy rain overwhelmed the drains and surface water flooded the ground-floor workshop.", amount: "£7,800");

        var (assessment, _) = await Assess(context);

        var exclusion = Single(assessment, FindingKind.PossibleExclusion);
        var citation = Assert.Single(exclusion.Citations);
        Assert.Equal(("TM-PW-OF-2026", "4.1", "Flood"), (citation.WordingId, citation.ClauseRef, citation.Heading));
        Assert.DoesNotContain(assessment.Findings, f => f.Kind == FindingKind.CoverageClause);
        Assert.Equal(ReviewRoute.SeniorReview, assessment.Route);
        Assert.Contains("possible exclusion", assessment.RouteReason);
    }

    [Fact]
    public async Task Flood_against_the_wording_with_the_flood_extension_is_covered_not_excluded()
    {
        var context = Assessing.Context(policy: "TM-CP-100007", lossDate: "2026-11-10",
            description: "The river flooded the yard and the loading bay.");

        var (assessment, _) = await Assess(context);

        Assert.DoesNotContain(assessment.Findings, f => f.Kind == FindingKind.PossibleExclusion);
        Assert.Equal(("TM-PW-CCF-2026", "3.5"), (Single(assessment, FindingKind.CoverageClause).Citations[0].WordingId, Single(assessment, FindingKind.CoverageClause).Citations[0].ClauseRef));
        Assert.Equal(ReviewRoute.StraightToHandler, assessment.Route);
    }

    [Fact]
    public async Task Unoccupied_for_more_than_30_days_with_a_burst_pipe_triggers_the_unoccupied_exclusion()
    {
        var context = Assessing.Context(policy: "TM-CP-100008", lossDate: "2026-11-10",
            description: "A pipe burst in our closed branch surgery, which had been unoccupied for 45 days.");

        var (assessment, _) = await Assess(context);

        Assert.Equal("4.3", Single(assessment, FindingKind.PossibleExclusion).Citations[0].ClauseRef);
        Assert.Equal(ReviewRoute.SeniorReview, assessment.Route);
    }

    [Theory]
    [InlineData("A pipe burst in the surgery, which had been unoccupied for 3 weeks.", false)]   // 21 days: inside the limit.
    [InlineData("A pipe burst in the surgery, which had been unoccupied for 6 weeks.", true)]    // 42 days.
    [InlineData("A pipe burst in the surgery, which had been vacant for 2 months.", true)]       // 60 days.
    [InlineData("A pipe burst in the surgery, which had been closed for 30 days.", false)]       // Exactly the limit is not over it.
    public async Task The_unoccupied_period_is_worked_out_from_days_weeks_or_months(string description, bool excluded)
    {
        var (assessment, _) = await Assess(Assessing.Context(policy: "TM-CP-100008", lossDate: "2026-11-10", description: description));

        Assert.Equal(excluded, assessment.Findings.Any(f => f.Kind == FindingKind.PossibleExclusion && f.Citations[0].Heading.StartsWith("Unoccupied", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_unoccupied_exclusion_does_not_apply_to_a_fire()
    {
        var context = Assessing.Context(policy: "TM-CP-100008", lossDate: "2026-11-10",
            description: "A fire started in the surgery, which had been unoccupied for 45 days.");

        var (assessment, _) = await Assess(context);

        Assert.DoesNotContain(assessment.Findings, f => f.Kind == FindingKind.PossibleExclusion);
        Assert.Equal("3.1", Single(assessment, FindingKind.CoverageClause).Citations[0].ClauseRef);
    }

    [Fact]
    public async Task Wear_and_tear_is_flagged_on_the_engineering_wording()
    {
        var context = Assessing.Context(policy: "TM-CP-100004", lossDate: "2026-10-20",
            description: "A hydraulic seal failed after years of gradual wear and the press brake stopped.");

        var (assessment, _) = await Assess(context);

        Assert.Equal(("TM-PW-EN-2026", "4.1"), (Single(assessment, FindingKind.PossibleExclusion).Citations[0].WordingId, Single(assessment, FindingKind.PossibleExclusion).Citations[0].ClauseRef));
        Assert.Equal("3.1", Single(assessment, FindingKind.CoverageClause).Citations[0].ClauseRef); // Breakdown.
        Assert.Equal(ReviewRoute.SeniorReview, assessment.Route);
    }

    [Fact]
    public async Task A_late_notification_is_read_from_the_wording_and_cites_the_condition()
    {
        // The Office wording allows 14 days; the Commercial Combined wording allows 30.
        var office = Assessing.Context(policy: "TM-CP-100006", lossDate: "2026-10-25", description: "A fire damaged the back office.", amount: "£6,000");
        var combined = Assessing.Context(policy: "TM-CP-100001", lossDate: "2026-10-25", description: "A fire damaged the workshop.");

        var (lateOffice, _) = await Assess(office);
        var (inTimeCombined, _) = await Assess(combined);

        var breach = Single(lateOffice, FindingKind.NotificationBreach);
        Assert.Equal(("TM-PW-OF-2026", "5.1"), (breach.Citations[0].WordingId, breach.Citations[0].ClauseRef));
        Assert.Contains("20 days", breach.Summary);
        Assert.Contains("allows 14 days", breach.Summary);
        Assert.Equal(ReviewRoute.SeniorReview, lateOffice.Route);
        Assert.DoesNotContain(inTimeCombined.Findings, f => f.Kind == FindingKind.NotificationBreach); // 20 days of 30.
    }

    [Fact]
    public async Task Notification_on_the_last_permitted_day_is_not_a_breach()
    {
        var onTime = Assessing.Context(policy: "TM-CP-100006", lossDate: "2026-10-31", description: "A fire damaged the back office."); // 14 days.
        var late = Assessing.Context(policy: "TM-CP-100006", lossDate: "2026-10-30", description: "A fire damaged the back office.");  // 15 days.

        Assert.DoesNotContain((await Assess(onTime)).Assessment.Findings, f => f.Kind == FindingKind.NotificationBreach);
        Assert.Contains((await Assess(late)).Assessment.Findings, f => f.Kind == FindingKind.NotificationBreach);
    }

    [Fact]
    public async Task A_matching_existing_claim_is_a_possible_duplicate_and_the_reference_comes_from_the_check()
    {
        var existing = new FakeExistingClaims(new ExistingClaim("0b1c", "review", "TM-CP-100001", new DateOnly(2026, 11, 3), 12_400m, "NeedsReview"));

        var (assessment, tools) = await Assess(Assessing.Context(), existing);

        var duplicate = Single(assessment, FindingKind.PossibleDuplicate);
        Assert.Equal("0b1c", duplicate.RelatedClaimRef);
        Assert.Empty(duplicate.Citations);
        Assert.Contains("0b1c", tools.ReturnedMatches.Keys);
        Assert.Equal(ReviewRoute.SeniorReview, assessment.Route);
        Assert.Contains("possible duplicate", assessment.RouteReason);
    }

    [Fact]
    public async Task Every_cited_clause_was_returned_by_the_search_in_this_run()
    {
        var context = Assessing.Context(policy: "TM-CP-100008", lossDate: "2026-10-05",
            description: "A pipe burst in our closed branch surgery, which had been unoccupied for 45 days.");

        var (assessment, tools) = await Assess(context);

        var cited = assessment.Findings.SelectMany(f => f.Citations).ToList();
        Assert.NotEmpty(cited);
        Assert.All(cited, c =>
        {
            Assert.True(tools.ReturnedClauses.TryGetValue($"{c.WordingId}/{c.ClauseRef}", out var returned));
            Assert.Equal(returned!.Text, c.Text);
        });
    }

    [Fact]
    public async Task The_wording_search_is_limited_to_the_wording_of_the_policy()
    {
        var search = new CountingSearch(Corpus.Search());
        var tools = Assessing.Tools(search: search);

        await new RuleBasedClaimAssessor().AssessAsync(Assessing.Context(policy: "TM-CP-100006"), tools, CancellationToken.None);

        Assert.All(search.Calls, c => Assert.Equal("TM-PW-OF-2026", c.WordingId));
    }

    [Fact]
    public async Task Without_an_identifiable_policy_nothing_is_assessed_and_no_search_is_made()
    {
        var search = new CountingSearch(Corpus.Search());
        var existing = new FakeExistingClaims();
        var tools = Assessing.Tools(search: search, existing: existing);

        var assessment = await new RuleBasedClaimAssessor().AssessAsync(Assessing.Context(policy: "TM-CP-999999"), tools, CancellationToken.None);

        Assert.Empty(assessment.Findings);
        Assert.Contains("could not be identified", Assert.Single(assessment.Notes));
        Assert.Empty(search.Calls);
        Assert.Empty(existing.Lookups);
    }

    [Fact]
    public async Task A_cause_the_rules_do_not_recognise_is_noted_and_goes_to_senior_review()
    {
        var (assessment, _) = await Assess(Assessing.Context(description: "Something odd happened in the office on Tuesday."));

        Assert.Empty(assessment.Findings);
        Assert.Contains(assessment.Notes, n => n.Contains("not recognised"));
        Assert.Equal(ReviewRoute.SeniorReview, assessment.Route);
    }

    [Fact]
    public async Task The_same_claim_always_gets_the_same_assessment()
    {
        var context = Assessing.Context(policy: "TM-CP-100008", lossDate: "2026-10-05",
            description: "A pipe burst in our closed branch surgery, which had been unoccupied for 45 days.");

        var first = (await Assess(context)).Assessment;
        var second = (await Assess(context)).Assessment;

        Assert.Equal(first.Findings.Select(f => (f.Kind, f.Summary, string.Join(',', f.Citations.Select(c => c.ClauseRef)))),
            second.Findings.Select(f => (f.Kind, f.Summary, string.Join(',', f.Citations.Select(c => c.ClauseRef)))));
        Assert.Equal((first.Route, first.RouteReason), (second.Route, second.RouteReason));
    }

    [Fact]
    public async Task The_assessor_only_proposes_it_records_a_proposal_and_writes_nothing()
    {
        var (assessment, tools) = await Assess(Assessing.Context());

        var proposal = Assert.Single(tools.Proposals);
        Assert.Equal("straight_to_handler", proposal.Route);
        Assert.Equal(ReviewRoute.StraightToHandler, assessment.Route);
    }
}
