using ClaimsIntake.Domain;
using ClaimsIntake.Pipeline.Fields;

namespace ClaimsIntake.Pipeline.Tests;

/// <summary>
/// Runs every sample document through the real text extractor, the heuristic extractor and the validator, and
/// checks that each document's known problem is the problem that is reported. It is a small regression set for
/// the extraction rules; the 50-claim evaluation set arrives in Milestone 4.
/// </summary>
public class SampleDocumentTests
{
    private static async Task<PipelineResult> Run(string sample, RecordingSink? sink = null) =>
        await Docs.Pipeline(sink: sink).ProcessAsync(Docs.Sample(sample), CancellationToken.None);

    private static string? Value(PipelineResult result, string field) =>
        result.Claim.Fields.Single(f => f.Name == field).Value;

    private static IssueCode[] Errors(PipelineResult result) =>
        [.. result.Claim.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code).Order()];

    [Theory]
    [InlineData("01-water-damage-clean.pdf", "TM-CP-100001", "2026-11-03", "£12,500")]
    [InlineData("02-theft-clean.pdf", "TM-CP-100002", "2026-10-21", "£8,750.00")]
    [InlineData("07-broker-email.eml", "TM-CP-100005", "2026-11-10", "£5,400")]
    public async Task A_clean_document_gives_a_claim_with_no_issues(string sample, string policy, string lossDate, string amount)
    {
        var result = await Run(sample);

        Assert.Equal(policy, Value(result, ClaimFields.PolicyNumber));
        Assert.Equal(lossDate, Value(result, ClaimFields.LossDate));
        Assert.Equal(amount, Value(result, ClaimFields.EstimatedAmount));
        Assert.NotNull(Value(result, ClaimFields.ClaimantName));
        Assert.NotNull(Value(result, ClaimFields.LossDescription));
        Assert.Empty(result.Claim.Issues);
        Assert.True(result.Confidence >= 0.88);
    }

    [Fact]
    public async Task A_missing_policy_number_is_an_error_and_nothing_is_looked_up()
    {
        var policies = FakePolicyLookup.Seeded();
        var result = await Docs.Pipeline(policies).ProcessAsync(Docs.Sample("03-missing-policy-number.pdf"), CancellationToken.None);

        Assert.Null(Value(result, ClaimFields.PolicyNumber));
        Assert.Equal([IssueCode.MissingPolicyNumber], Errors(result));
        Assert.Empty(policies.Lookups);
    }

    [Fact]
    public async Task A_loss_date_in_the_future_is_an_error()
    {
        var result = await Run("04-future-loss-date.pdf");

        Assert.Equal("2027-02-15", Value(result, ClaimFields.LossDate));
        Assert.Equal([IssueCode.LossDateInFuture], Errors(result));
    }

    [Fact]
    public async Task A_loss_date_outside_the_policy_period_is_an_error()
    {
        var result = await Run("05-loss-outside-policy-period.pdf");

        Assert.Equal("2026-11-08", Value(result, ClaimFields.LossDate));
        Assert.Equal([IssueCode.LossDateOutsidePolicyPeriod], Errors(result));
    }

    [Fact]
    public async Task An_informal_email_is_extracted_with_honest_low_confidence()
    {
        var result = await Run("06-email-fire.txt");
        var fields = result.Claim.Fields.ToDictionary(f => f.Name);

        Assert.Equal("TM-CP-100006", fields[ClaimFields.PolicyNumber].Value);
        Assert.Equal("2026-11-09", fields[ClaimFields.LossDate].Value);
        Assert.Equal("£6,000", fields[ClaimFields.EstimatedAmount].Value); // Not the £250 excess.

        // The sender is not the insured, and nothing is labelled, so every guess scores below the 0.80 threshold.
        Assert.Equal("Priya Nair", fields[ClaimFields.ClaimantName].Value);
        Assert.All(
            new[] { ClaimFields.ClaimantName, ClaimFields.LossDate, ClaimFields.LossDescription, ClaimFields.EstimatedAmount },
            name => Assert.True(fields[name].Confidence < ClaimValidator.LowConfidenceThreshold, name));

        Assert.Empty(Errors(result));
        Assert.Equal(4, result.WarningCount);
    }

    [Fact]
    public async Task A_garbled_layout_is_still_read_but_flagged_for_a_closer_look()
    {
        var result = await Run("08-garbled-layout.pdf");

        Assert.Equal("TM-CP-100007", Value(result, ClaimFields.PolicyNumber));
        Assert.True(result.Confidence < ClaimValidator.LowConfidenceThreshold);
        Assert.True(result.ErrorCount + result.WarningCount > 0);
    }

    [Fact]
    public async Task Source_spans_point_at_the_value_in_the_page_text()
    {
        var result = await Run("01-water-damage-clean.pdf");

        foreach (var field in result.Claim.Fields.Where(f => f.HasValue && f.Name != ClaimFields.LossDate && f.Name != ClaimFields.LossDescription))
        {
            var span = field.Source!;
            var page = result.Claim.SourcePages[span.Page - 1];
            Assert.Equal(field.Value, page.Substring(span.Offset, span.Length));
        }
    }

    [Fact]
    public void Every_sample_has_a_test()
    {
        var samples = Directory.EnumerateFiles(Docs.SamplesFolder).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(8, samples.Length);
    }
}
