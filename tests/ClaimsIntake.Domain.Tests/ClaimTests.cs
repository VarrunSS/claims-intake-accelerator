using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Security;

namespace ClaimsIntake.Domain.Tests;

public class ClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    private static Claim NewClaim(params ValidationIssue[] issues) =>
        new(
            Guid.NewGuid(),
            "inbox/claim.pdf",
            Now.AddHours(-2),
            [
                new ExtractedField(ClaimFields.PolicyNumber, " TM-CP-100001 ", 0.98),
                new ExtractedField(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.95),
            ],
            issues);

    private static readonly Approver Handler = new("oid-handler", "A Patel", [AppRoles.ClaimsHandler]);
    private static readonly Approver Senior = new("oid-senior", "B Jones", [AppRoles.SeniorHandler]);
    private static readonly Approver Nobody = new("oid-nobody", "C Reader", []);

    private static Claim Routed(ReviewRoute? route)
    {
        var claim = NewClaim();
        if (route is { } r)
        {
            claim.AttachAssessment(new ClaimAssessment("rules", [], r, "reason", []));
        }

        return claim;
    }

    private static ValidationIssue Error() =>
        new(IssueCode.MissingLossDate, IssueSeverity.Error, ClaimFields.LossDate, "No date of loss.");

    private static ValidationIssue Warning() =>
        new(IssueCode.LowConfidence, IssueSeverity.Warning, ClaimFields.ClaimantName, "Low confidence.");

    [Fact]
    public void A_new_claim_waits_for_review_with_no_decision()
    {
        var claim = NewClaim();

        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
        Assert.Null(claim.DecidedBy);
        Assert.Null(claim.DecidedAt);
    }

    [Fact]
    public void The_policy_number_is_trimmed_when_read()
    {
        Assert.Equal("TM-CP-100001", NewClaim().PolicyNumber);
    }

    [Fact]
    public void Approving_records_who_and_when()
    {
        var claim = NewClaim(Warning());

        claim.Approve(new Approver("  oid-handler  ", "  A Patel ", [AppRoles.ClaimsHandler]), Now);

        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.Equal("oid-handler", claim.DecidedBy);
        Assert.Equal("A Patel", claim.DecidedByName);
        Assert.Equal(AppRoles.ClaimsHandler, claim.DecidedAs);
        Assert.Equal(Now, claim.DecidedAt);
    }

    [Fact]
    public void A_claim_with_a_blocking_error_cannot_be_approved()
    {
        var claim = NewClaim(Error());

        var ex = Assert.Throws<InvalidClaimTransitionException>(() => claim.Approve(Handler, Now));

        Assert.Contains("data-quality errors", ex.Message);
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
    }

    [Fact]
    public void A_claim_with_a_blocking_error_can_still_be_rejected()
    {
        var claim = NewClaim(Error());

        claim.Reject(Handler, "No date of loss in the document.", Now);

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.Equal("No date of loss in the document.", claim.RejectionReason);
    }

    [Fact]
    public void Rejecting_needs_a_reason()
    {
        var claim = NewClaim();

        Assert.Throws<ArgumentException>(() => claim.Reject(Handler, "  ", Now));
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_decision_needs_an_identified_approver(string id)
    {
        Assert.Throws<ArgumentException>(() => new Approver(id, "Name", [AppRoles.ClaimsHandler]));
    }

    // Segregation of duties (SEC-03): every route and role pair, enforced in the domain.
    [Theory]
    [InlineData(ReviewRoute.StraightToHandler, AppRoles.ClaimsHandler, true)]
    [InlineData(ReviewRoute.StraightToHandler, AppRoles.SeniorHandler, true)]
    [InlineData(ReviewRoute.StraightToHandler, "none", false)]
    [InlineData(ReviewRoute.SeniorReview, AppRoles.ClaimsHandler, false)]
    [InlineData(ReviewRoute.SeniorReview, AppRoles.SeniorHandler, true)]
    [InlineData(ReviewRoute.SeniorReview, "none", false)]
    [InlineData(null, AppRoles.ClaimsHandler, true)] // No assessor ran: nothing flagged the claim, so a handler may approve it (ADR 0009).
    [InlineData(null, AppRoles.SeniorHandler, true)]
    [InlineData(null, "none", false)]
    public void Approval_needs_the_role_the_route_requires(ReviewRoute? route, string role, bool allowed)
    {
        var claim = Routed(route);
        var approver = role == "none" ? Nobody : role == AppRoles.SeniorHandler ? Senior : Handler;

        if (allowed)
        {
            claim.Approve(approver, Now);
            Assert.Equal(ClaimStatus.Approved, claim.Status);
            Assert.Equal(route, claim.DecidedOnRoute);
        }
        else
        {
            Assert.Throws<InsufficientRoleException>(() => claim.Approve(approver, Now));
            Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
            Assert.Null(claim.DecidedBy);
        }
    }

    [Fact]
    public void A_handler_may_reject_a_senior_review_claim_but_a_person_with_no_role_may_not()
    {
        var claim = Routed(ReviewRoute.SeniorReview);
        Assert.Throws<InsufficientRoleException>(() => claim.Reject(Nobody, "reason", Now));

        claim.Reject(Handler, "Exclusion applies.", Now);

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
    }

    [Fact]
    public void A_person_holding_both_roles_acts_as_a_senior_handler()
    {
        var both = new Approver("oid-both", null, [AppRoles.ClaimsHandler, AppRoles.SeniorHandler]);
        var claim = Routed(ReviewRoute.SeniorReview);

        claim.Approve(both, Now);

        Assert.Equal(AppRoles.SeniorHandler, claim.DecidedAs);
    }

    [Fact]
    public void The_display_name_is_cleaned_and_never_decides_anything()
    {
        var approver = new Approver("oid-x", "Eve\r\nSenior Handler", [AppRoles.ClaimsHandler]);

        Assert.Equal("Eve  Senior Handler", approver.DisplayName);
        Assert.False(approver.IsSenior);
    }

    [Fact]
    public void A_decided_claim_cannot_be_decided_again()
    {
        var approved = NewClaim();
        approved.Approve(Handler, Now);
        var rejected = NewClaim();
        rejected.Reject(Handler, "Duplicate.", Now);

        Assert.Throws<InvalidClaimTransitionException>(() => approved.Reject(Senior, "Changed my mind.", Now));
        Assert.Throws<InvalidClaimTransitionException>(() => approved.Approve(Senior, Now));
        Assert.Throws<InvalidClaimTransitionException>(() => rejected.Approve(Senior, Now));
        Assert.Equal("oid-handler", approved.DecidedBy);
    }

    [Fact]
    public void Only_an_approved_claim_can_be_marked_as_submitted()
    {
        var claim = NewClaim();
        Assert.Throws<InvalidClaimTransitionException>(() => claim.MarkSubmitted("LC-000001"));

        claim.Approve(Handler, Now);
        Assert.Throws<ArgumentException>(() => claim.MarkSubmitted(" "));
        claim.MarkSubmitted("LC-000001");

        Assert.Equal(ClaimStatus.Submitted, claim.Status);
        Assert.Equal("LC-000001", claim.LegacyReference);
    }

    [Fact]
    public void Warnings_do_not_block_approval()
    {
        Assert.False(NewClaim(Warning()).HasBlockingIssues);
        Assert.True(NewClaim(Warning(), Error()).HasBlockingIssues);
    }

    [Fact]
    public void A_claim_needs_an_id_and_a_source_document()
    {
        Assert.Throws<ArgumentException>(() => new Claim(Guid.Empty, "doc", Now, [], []));
        Assert.Throws<ArgumentException>(() => new Claim(Guid.NewGuid(), " ", Now, [], []));
    }
}
