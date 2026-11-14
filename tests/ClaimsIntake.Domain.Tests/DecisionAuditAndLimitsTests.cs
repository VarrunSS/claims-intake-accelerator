using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Domain.Security;
using ClaimsIntake.Domain.Telemetry;

namespace ClaimsIntake.Domain.Tests;

public class DecisionAuditAndLimitsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);
    private static readonly Approver Handler = new("oid-handler", "A Patel", [AppRoles.ClaimsHandler]);

    private static Claim NewClaim() =>
        new(Guid.NewGuid(), "inbox/Smith J - burst pipe.pdf", Now, [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.9)], []);

    private static ProposedClaim Proposed(int pages = 2, int pageLength = 100, int trail = 0) =>
        new("inbox/doc.pdf", Now,
            [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.9)],
            [],
            [.. Enumerable.Range(0, pages).Select(i => new string((char)('a' + i % 26), pageLength))],
            new ClaimAssessment("rules", [], ReviewRoute.StraightToHandler, "ok", []),
            [.. Enumerable.Range(0, trail).Select(i => new AuditEntry(Guid.Empty, "search_policy_wording",
                new Dictionary<string, string> { ["query"] = "[text:5 chars]" }, "1 clauses", 1.0, Now.AddMilliseconds(i)))]);

    [Fact]
    public void Decision_entries_hold_the_actor_role_route_and_outcome_but_no_name_or_reason_text()
    {
        var claim = NewClaim();
        claim.Reject(Handler, "A long private reason mentioning Mr Smith", Now);

        var entry = DecisionAudit.ForRejected(claim);

        Assert.Equal(DecisionAudit.Rejected, entry.Tool);
        Assert.Equal("oid-handler", entry.Arguments["actor_oid"]);
        Assert.Equal(AppRoles.ClaimsHandler, entry.Arguments["role"]);
        Assert.Equal("NoAssessment", entry.Arguments["route"]);
        Assert.Equal("41", entry.Arguments["reason_length"]);
        Assert.DoesNotContain(entry.Arguments.Values.Concat([entry.ResultSummary]), v => v.Contains("Smith") || v.Contains("Patel"));
    }

    [Fact]
    public void A_decision_entry_has_the_same_row_key_every_time_it_is_built()
    {
        var claim = NewClaim();
        claim.Approve(Handler, Now);

        Assert.Equal(DecisionAudit.ForApproved(claim).RowKey, DecisionAudit.ForApproved(claim).RowKey);
        Assert.EndsWith("-d-approved", DecisionAudit.ForApproved(claim).RowKey);
    }

    [Fact]
    public async Task Writing_the_same_entry_twice_to_the_in_memory_log_leaves_one_row()
    {
        var log = new InMemoryAuditLog();
        var claim = NewClaim();
        claim.Approve(Handler, Now);

        await log.AppendAsync(DecisionAudit.ForApproved(claim), CancellationToken.None);
        await log.AppendAsync(DecisionAudit.ForApproved(claim), CancellationToken.None);

        Assert.Single(await log.ForClaimAsync(claim.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Entries_without_a_key_get_one_from_their_content_so_the_same_call_is_never_stored_twice()
    {
        var log = new InMemoryAuditLog();
        var entry = new AuditEntry(Guid.NewGuid(), "lookup_policy", new Dictionary<string, string> { ["policy_number"] = "TM-CP-100001" }, "found", 1, Now);

        await log.AppendAsync(entry, CancellationToken.None);
        await log.AppendAsync(entry with { DurationMs = 5 }, CancellationToken.None);

        Assert.Single(await log.ForClaimAsync(entry.ClaimId, CancellationToken.None));
    }

    [Fact]
    public void A_proposed_claim_within_the_limits_is_returned_unchanged()
    {
        var proposed = Proposed();

        Assert.Same(proposed, proposed.WithinLimits());
        Assert.Same(proposed, proposed.FitToSize());
    }

    [Fact]
    public void A_document_over_the_page_limit_is_cut_with_a_blocking_issue_and_senior_review()
    {
        var proposed = Proposed(pages: IntakeLimits.MaxPages + 1);

        var cut = proposed.WithinLimits();

        Assert.Equal(IntakeLimits.MaxPages, cut.SourcePages.Count);
        var issue = Assert.Single(cut.Issues, i => i.Code == IssueCode.DocumentTooLarge);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(ReviewRoute.SeniorReview, cut.Assessment!.Route);
        Assert.True(cut.ToClaim().HasBlockingIssues);
    }

    [Fact]
    public void A_150_entry_audit_trail_is_cut_to_the_limit_with_a_note_not_refused()
    {
        var cut = Proposed(trail: 150).WithinLimits();

        Assert.Equal(IntakeLimits.MaxAuditEntries, cut.AuditTrail!.Count);
        Assert.Contains(cut.Assessment!.Notes, n => n.Contains("audit trail was cut"));
    }

    [Fact]
    public void A_300_KB_hand_off_is_cut_to_fit_under_the_service_bus_limit_and_keeps_its_fields_and_assessment()
    {
        var proposed = Proposed(pages: 3, pageLength: 100_000);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(proposed.ToJson()) > 256 * 1024);

        var fitted = proposed.FitToSize();

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(fitted.ToJson()) <= IntakeLimits.MaxHandoffBytes);
        Assert.Equal(proposed.Fields, fitted.Fields);
        Assert.Equal(proposed.DocumentId, fitted.DocumentId);
        Assert.Equal(proposed.ClaimId, fitted.ClaimId);
        Assert.Contains(fitted.Issues, i => i.Code == IssueCode.DocumentTooLarge && i.Message.Contains("original document"));
        Assert.Equal(ReviewRoute.SeniorReview, fitted.Assessment!.Route);
    }

    [Theory]
    [InlineData("/policies/TM-CP-100001", "/policies/{number}")]
    [InlineData("/policies/tm-cp-100001?x=1", "/policies/{number}")]
    [InlineData("/claims/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "/claims/{id}")]
    [InlineData("/claims/3f2504e0-4f89-11d3-9a0c-0305e82c3301/approve", "/claims/{id}/approve")]
    [InlineData("/claims/history?policyNumber=TM-CP-100001", "/claims/history")]
    [InlineData("/claims/LC-000001", "/claims/{value}")]
    [InlineData("/health/ready", "/health/ready")]
    [InlineData("/Smith%20J%20-%20burst%20pipe.pdf", "/{value}")]
    [InlineData("", "/")]
    public void Url_paths_become_route_templates_so_no_identifier_reaches_telemetry(string path, string expected)
    {
        Assert.Equal(expected, UrlScrubber.RouteTemplate(path));
    }
}
