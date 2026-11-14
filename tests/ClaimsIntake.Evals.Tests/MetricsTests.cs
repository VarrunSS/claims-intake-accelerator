using ClaimsIntake.Evals;

namespace ClaimsIntake.Evals.Tests;

public class NormalisationTests
{
    [Theory]
    [InlineData("2026-11-03", "2026-11-03")]
    [InlineData("3 November 2026", "2026-11-03")]
    [InlineData("3 Nov 2026", "2026-11-03")]
    [InlineData("03/11/2026", "2026-11-03")] // UK day first
    [InlineData("3/11/2026", "2026-11-03")]
    public void Dates_are_read_as_iso_with_uk_day_first(string text, string expected) =>
        Assert.Equal(expected, Normalisation.Date(text));

    [Theory]
    [InlineData("03/11/26")] // two-digit years are not read: a miss, not a guess
    [InlineData("31/02/2026")]
    [InlineData("soon")]
    [InlineData("")]
    [InlineData(null)]
    public void Text_that_is_not_a_date_gives_null(string? text) => Assert.Null(Normalisation.Date(text));

    [Theory]
    [InlineData("£12,500", 1_250_000)]
    [InlineData("8,750.00", 875_000)]
    [InlineData("£8,750.5", 875_050)]
    [InlineData("12500", 1_250_000)]
    public void Amounts_are_compared_in_pence(string text, long pence) => Assert.Equal(pence, Normalisation.Pence(text));

    [Theory]
    [InlineData("twelve thousand pounds")]
    [InlineData("GBP 15,200")]
    [InlineData("-5")]
    [InlineData(null)]
    public void Amounts_that_cannot_be_read_give_null_and_number_words_are_never_read(string? text) => Assert.Null(Normalisation.Pence(text));

    [Fact]
    public void A_decimal_amount_converts_to_the_same_pence() => Assert.Equal(1_250_000, Normalisation.Pence(12_500m));

    [Theory]
    [InlineData("Brindle & Marsh Joinery Ltd", "BRINDLE AND MARSH JOINERY LIMITED")]
    [InlineData("Wren & Co Estate Agents", "wren and co estate agents.")]
    [InlineData("Harcourt Dental Practice", "  Harcourt   Dental Practice ")]
    public void Names_ignore_case_ampersand_limited_punctuation_and_spacing(string a, string b) =>
        Assert.Equal(Normalisation.Name(a), Normalisation.Name(b));

    [Fact]
    public void Different_names_stay_different() =>
        Assert.NotEqual(Normalisation.Name("Brindle & Marsh Joinery Ltd"), Normalisation.Name("Brindle Marsh Joinery"));

    [Fact]
    public void A_policy_number_is_exact_after_trimming()
    {
        Assert.Equal(Normalisation.Policy("TM-CP-100001"), Normalisation.Policy("  TM-CP-100001 "));
        Assert.NotEqual(Normalisation.Policy("TM-CP-100001"), Normalisation.Policy("tm-cp-100001"));
        Assert.NotEqual(Normalisation.Policy("TM-CP-100001"), Normalisation.Policy("TM-CP-10001"));
    }
}

public class MetricsTests
{
    [Fact]
    public void Precision_recall_and_f1_follow_the_definitions()
    {
        var c = new Counts(8, 2, 2);

        Assert.Equal(0.8, c.Precision!.Value, 10);
        Assert.Equal(0.8, c.Recall!.Value, 10);
        Assert.Equal(0.8, c.F1!.Value, 10);
    }

    [Fact]
    public void F1_is_the_harmonic_mean_not_the_average()
    {
        var c = new Counts(6, 2, 4); // P = 0.75, R = 0.6

        Assert.Equal(2 * 0.75 * 0.6 / 1.35, c.F1!.Value, 10);
        Assert.True(c.F1 < (0.75 + 0.6) / 2);
    }

    [Fact]
    public void A_zero_denominator_is_undefined_not_zero_or_one()
    {
        Assert.Null(Counts.Zero.Precision);
        Assert.Null(Counts.Zero.Recall);
        Assert.Null(Counts.Zero.F1);
        Assert.Null(new Counts(0, 3, 0).Recall); // nothing was expected
        Assert.Null(new Counts(0, 0, 3).Precision); // nothing was predicted
    }

    [Fact]
    public void No_true_positives_with_errors_on_both_sides_is_f1_zero() => Assert.Equal(0.0, new Counts(0, 2, 2).F1);

    [Fact]
    public void Counts_add_for_micro_averaging() =>
        Assert.Equal(new Counts(5, 3, 1), Counts.Sum([new Counts(2, 1, 0), new Counts(3, 2, 1)]));

    [Theory]
    [InlineData("a", "a", FieldOutcome.Correct)]
    [InlineData("a", "b", FieldOutcome.Wrong)]
    [InlineData("a", null, FieldOutcome.Missed)]
    [InlineData(null, "b", FieldOutcome.Spurious)]
    [InlineData(null, null, FieldOutcome.BothAbsent)]
    [InlineData("", null, FieldOutcome.BothAbsent)]
    public void Fields_are_scored_strictly(string? expected, string? predicted, FieldOutcome outcome) =>
        Assert.Equal(outcome, Scoring.Compare(expected, predicted));

    [Fact]
    public void Numeric_fields_compare_with_the_same_rules()
    {
        Assert.Equal(FieldOutcome.Correct, Scoring.Compare<long>(100, 100));
        Assert.Equal(FieldOutcome.Wrong, Scoring.Compare<long>(100, 31));
        Assert.Equal(FieldOutcome.Missed, Scoring.Compare<long>(100, null));
        Assert.Equal(FieldOutcome.Spurious, Scoring.Compare<long>(null, 100));
    }

    [Fact]
    public void A_wrong_value_is_one_false_positive_and_one_false_negative()
    {
        Assert.Equal(new Counts(0, 1, 1), Scoring.ToCounts(FieldOutcome.Wrong));
        Assert.Equal(new Counts(1, 0, 0), Scoring.ToCounts(FieldOutcome.Correct));
        Assert.Equal(new Counts(0, 1, 0), Scoring.ToCounts(FieldOutcome.Spurious));
        Assert.Equal(new Counts(0, 0, 1), Scoring.ToCounts(FieldOutcome.Missed));
        Assert.Equal(Counts.Zero, Scoring.ToCounts(FieldOutcome.BothAbsent));
    }

    [Fact]
    public void Sets_are_compared_as_multisets()
    {
        var c = Scoring.CompareSets(["a", "b", "b", "c"], ["b", "b", "b", "d"]);

        Assert.Equal(new Counts(2, 2, 2), c);
    }

    [Fact]
    public void Percentiles_use_the_nearest_rank()
    {
        var ten = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();
        var twenty = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();

        Assert.Equal(5, Scoring.Percentile(ten, 50));
        Assert.Equal(10, Scoring.Percentile(ten, 95));
        Assert.Equal(19, Scoring.Percentile(twenty, 95));
        Assert.Equal(7, Scoring.Percentile([7], 95));
        Assert.Null(Scoring.Percentile([], 50));
    }

    [Fact]
    public void The_confusion_matrix_gives_accuracy_precision_and_recall()
    {
        var m = new ConfusionMatrix(["StraightToHandler", "SeniorReview"]);
        for (var i = 0; i < 6; i++) m.Add("StraightToHandler", "StraightToHandler");
        for (var i = 0; i < 2; i++) m.Add("StraightToHandler", "SeniorReview");
        m.Add("SeniorReview", "StraightToHandler");
        for (var i = 0; i < 3; i++) m.Add("SeniorReview", "SeniorReview");

        Assert.Equal(12, m.Total);
        Assert.Equal(9, m.Correct);
        Assert.Equal(0.75, m.Accuracy);
        Assert.Equal(6 / 7.0, m.PrecisionOf("StraightToHandler")!.Value, 10); // 7 predicted, 6 right
        Assert.Equal(6 / 8.0, m.RecallOf("StraightToHandler")!.Value, 10);
        Assert.Equal(3 / 5.0, m.PrecisionOf("SeniorReview")!.Value, 10);
        Assert.Equal(3 / 4.0, m.RecallOf("SeniorReview")!.Value, 10);
        Assert.Equal(1, m.Get("SeniorReview", "StraightToHandler"));
    }

    [Fact]
    public void An_empty_matrix_has_no_accuracy() => Assert.Null(new ConfusionMatrix(["a"]).Accuracy);
}
