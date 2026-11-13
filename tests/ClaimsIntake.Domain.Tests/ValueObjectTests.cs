using ClaimsIntake.Domain;

namespace ClaimsIntake.Domain.Tests;

public class ValueObjectTests
{
    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Confidence_must_be_between_zero_and_one(double confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractedField("loss_date", "2026-11-03", confidence));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void The_confidence_limits_are_allowed(double confidence)
    {
        Assert.Equal(confidence, new ExtractedField("loss_date", "2026-11-03", confidence).Confidence);
    }

    [Fact]
    public void A_field_needs_a_name()
    {
        Assert.Throws<ArgumentException>(() => new ExtractedField(" ", "x", 0.5));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("x", true)]
    public void A_field_has_a_value_only_when_it_is_not_blank(string? value, bool expected)
    {
        Assert.Equal(expected, new ExtractedField("f", value, 0.5).HasValue);
    }

    [Fact]
    public void A_source_span_rejects_impossible_positions()
    {
        Assert.Throws<ArgumentException>(() => new SourceSpan("", 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSpan("doc", 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSpan("doc", 1, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSpan("doc", 1, 0, -1));
    }

    [Fact]
    public void A_policy_covers_both_its_first_and_last_day_but_not_the_days_around_them()
    {
        var policy = new PolicyReference("TM-CP-1", "Insured", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.True(policy.Covers(new DateOnly(2026, 1, 1)));
        Assert.True(policy.Covers(new DateOnly(2026, 12, 31)));
        Assert.False(policy.Covers(new DateOnly(2025, 12, 31)));
        Assert.False(policy.Covers(new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void A_policy_cannot_end_before_it_starts()
    {
        Assert.Throws<ArgumentException>(() =>
            new PolicyReference("TM-CP-1", "Insured", new DateOnly(2026, 6, 1), new DateOnly(2026, 5, 31)));
    }
}
