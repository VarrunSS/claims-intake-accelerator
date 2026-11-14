namespace ClaimsIntake.Evals;

/// <summary>
/// True positives, false positives and false negatives, and the precision, recall and F1 they give. A ratio whose
/// denominator is zero is null ("not defined"), never 0 or 1: a field that never appears in a tag's cases has no recall.
/// </summary>
public sealed record Counts(int TruePositives, int FalsePositives, int FalseNegatives)
{
    public static readonly Counts Zero = new(0, 0, 0);

    public double? Precision => Ratio(TruePositives, TruePositives + FalsePositives);

    public double? Recall => Ratio(TruePositives, TruePositives + FalseNegatives);

    /// <summary>The harmonic mean of precision and recall. Null when either is undefined; 0 when both are 0.</summary>
    public double? F1
    {
        get
        {
            if (Precision is not { } p || Recall is not { } r)
            {
                return null;
            }

            return p + r == 0 ? 0 : 2 * p * r / (p + r);
        }
    }

    public static Counts operator +(Counts a, Counts b) =>
        new(a.TruePositives + b.TruePositives, a.FalsePositives + b.FalsePositives, a.FalseNegatives + b.FalseNegatives);

    public static Counts Sum(IEnumerable<Counts> items) => items.Aggregate(Zero, (a, b) => a + b);

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : numerator / (double)denominator;
}

/// <summary>The outcome of comparing one predicted value with one expected value.</summary>
public enum FieldOutcome
{
    /// <summary>Both present and equal after normalisation: a true positive.</summary>
    Correct,

    /// <summary>Both present and different: a false positive and a false negative, as in strict extraction scoring.</summary>
    Wrong,

    /// <summary>A value where none was expected: a false positive.</summary>
    Spurious,

    /// <summary>No value where one was expected: a false negative.</summary>
    Missed,

    /// <summary>Neither side has a value. Counts for nothing, because absence correctly reported is not a hit.</summary>
    BothAbsent,
}

public static class Scoring
{
    /// <param name="expected">The normalised expected value, or null when none is expected.</param>
    /// <param name="predicted">The normalised predicted value, or null when the pipeline found none.</param>
    public static FieldOutcome Compare<T>(T? expected, T? predicted) where T : struct, IEquatable<T> =>
        (expected, predicted) switch
        {
            (null, null) => FieldOutcome.BothAbsent,
            (null, _) => FieldOutcome.Spurious,
            (_, null) => FieldOutcome.Missed,
            var (e, p) => e.Value.Equals(p.Value) ? FieldOutcome.Correct : FieldOutcome.Wrong,
        };

    public static FieldOutcome Compare(string? expected, string? predicted)
    {
        var e = string.IsNullOrEmpty(expected) ? null : expected;
        var p = string.IsNullOrEmpty(predicted) ? null : predicted;
        return (e, p) switch
        {
            (null, null) => FieldOutcome.BothAbsent,
            (null, _) => FieldOutcome.Spurious,
            (_, null) => FieldOutcome.Missed,
            _ => string.Equals(e, p, StringComparison.Ordinal) ? FieldOutcome.Correct : FieldOutcome.Wrong,
        };
    }

    public static Counts ToCounts(FieldOutcome outcome) => outcome switch
    {
        FieldOutcome.Correct => new(1, 0, 0),
        FieldOutcome.Wrong => new(0, 1, 1),
        FieldOutcome.Spurious => new(0, 1, 0),
        FieldOutcome.Missed => new(0, 0, 1),
        _ => Counts.Zero,
    };

    /// <summary>
    /// Compares two collections as multisets of keys: each key present on both sides is a true positive (up to the smaller
    /// count), the rest of the predicted side are false positives and the rest of the expected side false negatives.
    /// </summary>
    public static Counts CompareSets(IEnumerable<string> expected, IEnumerable<string> predicted)
    {
        var e = expected.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var p = predicted.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var tp = e.Keys.Intersect(p.Keys).Sum(k => Math.Min(e[k], p[k]));
        return new Counts(tp, p.Values.Sum() - tp, e.Values.Sum() - tp);
    }

    /// <summary>Nearest-rank percentile: the smallest value with at least p percent of the values at or below it.</summary>
    public static double? Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}

/// <summary>A two-by-two (or larger) table of expected route against predicted route.</summary>
public sealed class ConfusionMatrix(IReadOnlyList<string> labels)
{
    private readonly Dictionary<(string Expected, string Predicted), int> _cells = [];

    public IReadOnlyList<string> Labels { get; } = labels;

    public void Add(string expected, string predicted) =>
        _cells[(expected, predicted)] = _cells.GetValueOrDefault((expected, predicted)) + 1;

    public int Get(string expected, string predicted) => _cells.GetValueOrDefault((expected, predicted));

    public int Total => _cells.Values.Sum();

    public int Correct => Labels.Sum(l => Get(l, l));

    public double? Accuracy => Total == 0 ? null : Correct / (double)Total;

    /// <summary>Of the cases predicted as <paramref name="label"/>, the share that were.</summary>
    public double? PrecisionOf(string label)
    {
        var predicted = Labels.Sum(e => Get(e, label));
        return predicted == 0 ? null : Get(label, label) / (double)predicted;
    }

    /// <summary>Of the cases that are <paramref name="label"/>, the share predicted as it.</summary>
    public double? RecallOf(string label)
    {
        var actual = Labels.Sum(p => Get(label, p));
        return actual == 0 ? null : Get(label, label) / (double)actual;
    }
}
