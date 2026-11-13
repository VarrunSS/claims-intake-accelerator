using ClaimsIntake.Domain;

namespace ClaimsIntake.Domain.Tests;

public class ClaimValidatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 13);

    private static readonly PolicyReference Policy =
        new("TM-CP-100001", "Brindle & Marsh Joinery Ltd", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

    private static Dictionary<string, ExtractedField> Fields(
        string? policyNumber = "TM-CP-100001", string? lossDate = "2026-11-03", string? amount = null, double confidence = 0.95)
    {
        var fields = new List<ExtractedField>
        {
            new(ClaimFields.PolicyNumber, policyNumber, confidence),
            new(ClaimFields.LossDate, lossDate, confidence),
            new(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", confidence),
        };
        if (amount is not null)
        {
            fields.Add(new(ClaimFields.EstimatedAmount, amount, confidence));
        }

        return fields.ToDictionary(f => f.Name);
    }

    private static IReadOnlyList<ValidationIssue> Validate(Dictionary<string, ExtractedField> fields, PolicyReference? policy = null) =>
        ClaimValidator.Validate(fields, policy ?? Policy, Today);

    [Fact]
    public void A_complete_claim_inside_the_policy_period_has_no_issues()
    {
        Assert.Empty(Validate(Fields()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_policy_number_is_a_blocking_error(string? policyNumber)
    {
        var issues = ClaimValidator.Validate(Fields(policyNumber: policyNumber), policy: null, Today);

        var issue = Assert.Single(issues);
        Assert.Equal(IssueCode.MissingPolicyNumber, issue.Code);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(ClaimFields.PolicyNumber, issue.Field);
    }

    [Fact]
    public void A_policy_number_field_that_is_absent_counts_as_missing()
    {
        var fields = Fields();
        fields.Remove(ClaimFields.PolicyNumber);

        var issues = ClaimValidator.Validate(fields, policy: null, Today);

        Assert.Contains(issues, i => i.Code == IssueCode.MissingPolicyNumber);
    }

    [Fact]
    public void A_policy_number_that_is_not_in_the_claims_system_is_flagged()
    {
        var issues = ClaimValidator.Validate(Fields(policyNumber: "TM-CP-999999"), policy: null, Today);

        var issue = Assert.Single(issues);
        Assert.Equal(IssueCode.UnknownPolicy, issue.Code);
        Assert.Contains("TM-CP-999999", issue.Message);
    }

    [Fact]
    public void A_loss_date_in_the_future_is_a_blocking_error()
    {
        var issues = Validate(Fields(lossDate: "2026-11-14"));

        var issue = Assert.Single(issues);
        Assert.Equal(IssueCode.LossDateInFuture, issue.Code);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void A_loss_date_of_today_is_accepted()
    {
        Assert.Empty(Validate(Fields(lossDate: "2026-11-13")));
    }

    [Theory]
    [InlineData("2025-12-31")]
    [InlineData("2027-01-01")]
    public void A_loss_date_outside_the_policy_period_is_a_blocking_error(string lossDate)
    {
        // 2027-01-01 is also in the future, so check the policy-period rule specifically.
        var issues = Validate(Fields(lossDate: lossDate));

        var issue = Assert.Single(issues, i => i.Code == IssueCode.LossDateOutsidePolicyPeriod);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("2026-01-01", issue.Message);
        Assert.Contains("2026-12-31", issue.Message);
    }

    [Theory]
    [InlineData("2026-01-01")]
    [InlineData("2026-12-31")]
    public void The_first_and_last_days_of_the_policy_period_are_covered(string lossDate)
    {
        // 2026-12-31 is after "today" here, so it is also a future date; only the period rule is asserted.
        var issues = Validate(Fields(lossDate: lossDate));

        Assert.DoesNotContain(issues, i => i.Code == IssueCode.LossDateOutsidePolicyPeriod);
    }

    [Fact]
    public void The_policy_period_is_not_checked_when_the_policy_is_unknown()
    {
        var issues = ClaimValidator.Validate(Fields(lossDate: "2019-05-05"), policy: null, Today);

        Assert.DoesNotContain(issues, i => i.Code == IssueCode.LossDateOutsidePolicyPeriod);
    }

    [Fact]
    public void A_missing_loss_date_is_a_blocking_error()
    {
        var issue = Assert.Single(Validate(Fields(lossDate: null)));

        Assert.Equal(IssueCode.MissingLossDate, issue.Code);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
    }

    [Theory]
    [InlineData("last Tuesday")]
    [InlineData("31/02/2026")]
    [InlineData("2026-13-01")]
    public void A_loss_date_that_is_not_a_date_is_a_blocking_error(string lossDate)
    {
        var issue = Assert.Single(Validate(Fields(lossDate: lossDate)));

        Assert.Equal(IssueCode.UnreadableLossDate, issue.Code);
    }

    [Theory]
    [InlineData("03/11/2026")]
    [InlineData("3/11/2026")]
    [InlineData("3 November 2026")]
    [InlineData("3 Nov 2026")]
    [InlineData(" 2026-11-03 ")]
    public void Day_first_UK_dates_and_ISO_dates_are_read_the_same_way(string text)
    {
        Assert.True(ClaimValidator.TryParseDate(text, out var date));
        Assert.Equal(new DateOnly(2026, 11, 3), date);
    }

    [Fact]
    public void Slash_dates_are_read_day_first_not_month_first()
    {
        // 04/05/2026 is 4 May in the UK. Reading it as 5 April would silently change the cover check.
        Assert.True(ClaimValidator.TryParseDate("04/05/2026", out var date));
        Assert.Equal(new DateOnly(2026, 5, 4), date);
    }

    [Theory]
    [InlineData("12,500")]
    [InlineData("£12,500.00")]
    [InlineData("0")]
    public void A_valid_estimated_amount_raises_no_issue(string amount)
    {
        Assert.Empty(Validate(Fields(amount: amount)));
    }

    [Theory]
    [InlineData("3,2OO")]
    [InlineData("-50")]
    [InlineData("a lot")]
    public void An_invalid_estimated_amount_is_a_warning_only(string amount)
    {
        var issue = Assert.Single(Validate(Fields(amount: amount)));

        Assert.Equal(IssueCode.InvalidAmount, issue.Code);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void A_field_below_the_confidence_threshold_is_flagged_as_a_warning()
    {
        var fields = Fields();
        fields[ClaimFields.ClaimantName] = new(ClaimFields.ClaimantName, "Brindle & Marsh", 0.79);

        var issue = Assert.Single(Validate(fields));

        Assert.Equal(IssueCode.LowConfidence, issue.Code);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal(ClaimFields.ClaimantName, issue.Field);
    }

    [Fact]
    public void A_field_exactly_at_the_confidence_threshold_is_not_flagged()
    {
        var fields = Fields(confidence: ClaimValidator.LowConfidenceThreshold);

        Assert.Empty(Validate(fields));
    }

    [Fact]
    public void A_field_with_no_value_is_not_also_flagged_for_low_confidence()
    {
        var issues = ClaimValidator.Validate(Fields(policyNumber: null, confidence: 0.1), policy: null, Today);

        Assert.DoesNotContain(issues, i => i.Code == IssueCode.LowConfidence && i.Field == ClaimFields.PolicyNumber);
    }

    [Fact]
    public void Several_problems_are_all_reported()
    {
        var issues = ClaimValidator.Validate(Fields(policyNumber: null, lossDate: "2099-01-01"), policy: null, Today);

        Assert.Contains(issues, i => i.Code == IssueCode.MissingPolicyNumber);
        Assert.Contains(issues, i => i.Code == IssueCode.LossDateInFuture);
    }
}
