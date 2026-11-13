using ClaimsIntake.Domain;

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

        claim.Approve("  a.patel  ", Now);

        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.Equal("a.patel", claim.DecidedBy);
        Assert.Equal(Now, claim.DecidedAt);
    }

    [Fact]
    public void A_claim_with_a_blocking_error_cannot_be_approved()
    {
        var claim = NewClaim(Error());

        var ex = Assert.Throws<InvalidClaimTransitionException>(() => claim.Approve("a.patel", Now));

        Assert.Contains("data-quality errors", ex.Message);
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
    }

    [Fact]
    public void A_claim_with_a_blocking_error_can_still_be_rejected()
    {
        var claim = NewClaim(Error());

        claim.Reject("a.patel", "No date of loss in the document.", Now);

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.Equal("No date of loss in the document.", claim.RejectionReason);
    }

    [Fact]
    public void Rejecting_needs_a_reason()
    {
        var claim = NewClaim();

        Assert.Throws<ArgumentException>(() => claim.Reject("a.patel", "  ", Now));
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_decision_needs_a_named_reviewer(string reviewer)
    {
        var claim = NewClaim();

        Assert.Throws<ArgumentException>(() => claim.Approve(reviewer, Now));
        Assert.Throws<ArgumentException>(() => claim.Reject(reviewer, "reason", Now));
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
    }

    [Fact]
    public void A_decided_claim_cannot_be_decided_again()
    {
        var approved = NewClaim();
        approved.Approve("a.patel", Now);
        var rejected = NewClaim();
        rejected.Reject("a.patel", "Duplicate.", Now);

        Assert.Throws<InvalidClaimTransitionException>(() => approved.Reject("b.jones", "Changed my mind.", Now));
        Assert.Throws<InvalidClaimTransitionException>(() => approved.Approve("b.jones", Now));
        Assert.Throws<InvalidClaimTransitionException>(() => rejected.Approve("b.jones", Now));
        Assert.Equal("a.patel", approved.DecidedBy);
    }

    [Fact]
    public void Only_an_approved_claim_can_be_marked_as_submitted()
    {
        var claim = NewClaim();
        Assert.Throws<InvalidClaimTransitionException>(claim.MarkSubmitted);

        claim.Approve("a.patel", Now);
        claim.MarkSubmitted();

        Assert.Equal(ClaimStatus.Submitted, claim.Status);
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
