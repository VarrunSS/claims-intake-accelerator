using System.Text.Json;
using ClaimsIntake.Domain.Intake;

namespace ClaimsIntake.Domain.Tests;

public class IntakeContractTests
{
    private static ProposedClaim Proposed(string documentId = "inbox/a.pdf") => new(
        documentId,
        new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
        [
            new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.97, new SourceSpan(documentId, 1, 15, 12)),
            new ExtractedField(ClaimFields.LossDescription, null, 0.0),
        ],
        [new ValidationIssue(IssueCode.MissingLossDate, IssueSeverity.Error, ClaimFields.LossDate, "No date of loss.")],
        ["Policy number: TM-CP-100001"]);

    [Fact]
    public void A_proposed_claim_survives_a_json_round_trip()
    {
        var original = Proposed();

        var back = ProposedClaim.FromJson(original.ToJson());

        Assert.Equal(original.DocumentId, back.DocumentId);
        Assert.Equal(original.ReceivedAt, back.ReceivedAt);
        Assert.Equal(original.Fields, back.Fields);
        Assert.Equal(original.Issues, back.Issues);
        Assert.Equal(original.SourcePages, back.SourcePages);
    }

    [Fact]
    public void The_json_uses_camel_case_names_and_enum_names_so_other_languages_can_read_it()
    {
        var json = Proposed().ToJson();

        Assert.Contains("\"documentId\"", json);
        Assert.Contains("\"sourcePages\"", json);
        Assert.Contains("\"code\":\"MissingLossDate\"", json);
        Assert.Contains("\"severity\":\"Error\"", json);
    }

    [Fact]
    public void A_confidence_outside_zero_to_one_is_refused_when_reading()
    {
        var json = Proposed().ToJson().Replace("0.97", "1.7", StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => ProposedClaim.FromJson(json));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not json")]
    public void Nonsense_is_refused_when_reading(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ProposedClaim.FromJson(json));
    }

    [Fact]
    public void The_claim_id_comes_from_the_document_id_so_a_repeat_is_the_same_claim()
    {
        Assert.Equal(Proposed("inbox/a.pdf").ClaimId, Proposed("inbox/a.pdf").ClaimId);
        Assert.NotEqual(Proposed("inbox/a.pdf").ClaimId, Proposed("inbox/b.pdf").ClaimId);
        Assert.NotEqual(Guid.Empty, ClaimIds.ForDocument("inbox/a.pdf"));
        Assert.Throws<ArgumentException>(() => ClaimIds.ForDocument(" "));
    }

    [Fact]
    public void Turning_a_proposal_into_a_claim_keeps_the_text_and_starts_it_awaiting_review()
    {
        var proposed = Proposed();

        var claim = proposed.ToClaim();

        Assert.Equal(proposed.ClaimId, claim.Id);
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
        Assert.Equal(["Policy number: TM-CP-100001"], claim.SourcePages);
        Assert.True(claim.HasBlockingIssues);
        Assert.Null(claim.LegacyReference);
    }

    [Fact]
    public void Confidence_is_that_of_the_weakest_field_that_was_found()
    {
        ExtractedField[] fields =
        [
            new(ClaimFields.PolicyNumber, "x", 0.97),
            new(ClaimFields.LossDate, "y", 0.62),
            new(ClaimFields.LossDescription, null, 0.0), // Not found: does not drag the claim to zero.
        ];

        Assert.Equal(0.62, ClaimConfidence.Lowest(fields));
        Assert.Equal(0.0, ClaimConfidence.Lowest([]));
        Assert.Equal(0.0, ClaimConfidence.Lowest([new ExtractedField(ClaimFields.LossDate, null, 0.0)]));
        Assert.Equal(0.62, proposedClaimWith(fields).MinConfidence);

        static Claim proposedClaimWith(ExtractedField[] f) => new(Guid.NewGuid(), "d", DateTimeOffset.UnixEpoch, f, []);
    }

    [Fact]
    public void A_claim_without_source_pages_is_allowed_for_older_callers()
    {
        var claim = new Claim(Guid.NewGuid(), "d", DateTimeOffset.UnixEpoch, [], []);

        Assert.Empty(claim.SourcePages);
    }
}
