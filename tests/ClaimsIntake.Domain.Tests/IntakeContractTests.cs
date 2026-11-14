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

public class AssessmentContractTests
{
    private static ClaimsIntake.Domain.Assessment.ClaimAssessment Assessment() => new(
        "rules",
        [
            new(ClaimsIntake.Domain.Assessment.FindingKind.PossibleExclusion, "Flood may be excluded.",
                [new("TM-PW-OF-2026", "4.1", "Flood", "We will not pay for flood.")]),
            new(ClaimsIntake.Domain.Assessment.FindingKind.PossibleDuplicate, "Matches LC-000001.", [], "LC-000001"),
        ],
        ClaimsIntake.Domain.Assessment.ReviewRoute.SeniorReview, "Needs senior review.", ["a note"]);

    private static ProposedClaim WithAssessment() => new(
        "inbox/a.pdf", new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
        [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.97)], [], ["page"],
        Assessment(),
        [new ClaimsIntake.Domain.Assessment.AuditEntry(Guid.NewGuid(), "lookup_policy", new Dictionary<string, string> { ["policy_number"] = "TM-CP-100001" },
            "found", 2.5, new DateTimeOffset(2026, 11, 14, 9, 0, 1, TimeSpan.Zero))]);

    [Fact]
    public void An_assessment_and_audit_trail_survive_a_json_round_trip_with_enum_names()
    {
        var original = WithAssessment();

        var json = original.ToJson();
        var back = ProposedClaim.FromJson(json);

        Assert.Contains("\"kind\":\"PossibleExclusion\"", json);
        Assert.Contains("\"route\":\"SeniorReview\"", json);
        Assert.Equal(original.Assessment!.Findings.Select(f => (f.Kind, f.Summary, f.RelatedClaimRef)), back.Assessment!.Findings.Select(f => (f.Kind, f.Summary, f.RelatedClaimRef)));
        Assert.Equal(original.Assessment.Findings[0].Citations, back.Assessment.Findings[0].Citations);
        Assert.Equal(original.AuditTrail![0].Arguments, back.AuditTrail![0].Arguments);
        Assert.Equal(original.AuditTrail[0].Timestamp, back.AuditTrail[0].Timestamp);
    }

    [Fact]
    public void A_message_from_before_assessments_existed_still_reads_with_none()
    {
        var old = """{"documentId":"inbox/a.pdf","receivedAt":"2026-11-14T09:00:00Z","fields":[],"issues":[],"sourcePages":[]}""";

        var claim = ProposedClaim.FromJson(old);

        Assert.Null(claim.Assessment);
        Assert.Null(claim.AuditTrail);
    }

    [Fact]
    public void The_assessment_reaches_the_claim_but_never_changes_its_status()
    {
        var claim = WithAssessment().ToClaim();

        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
        Assert.Equal(ClaimsIntake.Domain.Assessment.ReviewRoute.SeniorReview, claim.Assessment!.Route);
        Assert.False(claim.HasBlockingIssues); // A senior-review route is advice, not a blocking issue. Only a senior handler may act on it.
        claim.Approve(new ClaimsIntake.Domain.Security.Approver("oid-senior", "Senior", [ClaimsIntake.Domain.Security.AppRoles.SeniorHandler]), DateTimeOffset.UnixEpoch);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
    }

    [Fact]
    public void A_claim_without_an_assessment_has_none_and_one_cannot_be_attached_as_null()
    {
        var claim = new Claim(Guid.NewGuid(), "d", DateTimeOffset.UnixEpoch, [], []);

        Assert.Null(claim.Assessment);
        Assert.Throws<ArgumentNullException>(() => claim.AttachAssessment(null!));
    }

    [Theory]
    [InlineData("£12,500", true, 12500)]
    [InlineData("8,750.00", true, 8750)]
    [InlineData(" £0 ", true, 0)]
    [InlineData("-5", false, 0)]
    [InlineData("about six grand", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void Amounts_are_parsed_the_way_the_validator_reads_them(string? text, bool ok, int expected)
    {
        Assert.Equal(ok, ClaimValidator.TryParseAmount(text, out var amount));
        Assert.Equal(expected, amount);
    }

    [Fact]
    public void A_policy_reference_may_carry_a_wording_and_an_excess_and_equality_still_holds()
    {
        var plain = new PolicyReference("TM-CP-100001", "X", new(2026, 1, 1), new(2026, 12, 31));
        var full = plain with { };
        var withWording = new PolicyReference("TM-CP-100001", "X", new(2026, 1, 1), new(2026, 12, 31), "TM-PW-CC-2026", 500m);

        Assert.Null(plain.WordingId);
        Assert.Equal(plain, full);
        Assert.NotEqual(plain, withWording);
        Assert.Equal(("TM-PW-CC-2026", 500m), (withWording.WordingId, withWording.ExcessGbp));
    }
}
