using System.Globalization;
using System.Text.Json;

namespace ClaimsIntake.Evals;

/// <summary>A bound on one metric: at least <see cref="Min"/> or at most <see cref="Max"/>.</summary>
public sealed record Bound(double? Min = null, double? Max = null);

/// <summary>The contents of <c>evals/thresholds.json</c>.</summary>
public sealed record Thresholds(string? Description, string? Split, IReadOnlyDictionary<string, Bound> Metrics)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public static Thresholds Load(string path) =>
        JsonSerializer.Deserialize<Thresholds>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is empty.");
}

/// <summary>One threshold compared with the measured value.</summary>
public sealed record GateCheck(string Metric, string Requirement, double? Actual, bool Passed, string Reason);

public sealed record GateResult(IReadOnlyList<GateCheck> Checks)
{
    public bool Passed => Checks.All(c => c.Passed);

    public IEnumerable<GateCheck> Failures => Checks.Where(c => !c.Passed);
}

public static class Gate
{
    /// <summary>
    /// Compares every threshold with the measured metrics. A metric that was not measured fails its threshold (nothing to
    /// compare is not a pass), and so does a threshold that names a metric the run does not produce, since a typo in
    /// thresholds.json must not turn the gate off.
    /// </summary>
    public static GateResult Evaluate(Thresholds thresholds, IReadOnlyDictionary<string, double?> metrics)
    {
        var checks = new List<GateCheck>();
        foreach (var (name, bound) in thresholds.Metrics.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var requirement = Describe(bound);
            if (!metrics.TryGetValue(name, out var actual))
            {
                checks.Add(new GateCheck(name, requirement, null, false, "unknown metric"));
                continue;
            }

            if (actual is not { } value)
            {
                checks.Add(new GateCheck(name, requirement, null, false, "not measured"));
                continue;
            }

            if (bound.Min is { } min && value < min)
            {
                checks.Add(new GateCheck(name, requirement, value, false, $"{Format(value)} is below the minimum {Format(min)}"));
            }
            else if (bound.Max is { } max && value > max)
            {
                checks.Add(new GateCheck(name, requirement, value, false, $"{Format(value)} is above the maximum {Format(max)}"));
            }
            else
            {
                checks.Add(new GateCheck(name, requirement, value, true, "ok"));
            }
        }

        return new GateResult(checks);
    }

    public static string Describe(Bound bound) => (bound.Min, bound.Max) switch
    {
        ({ } min, { } max) => $"{Format(min)} to {Format(max)}",
        ({ } min, null) => $">= {Format(min)}",
        (null, { } max) => $"<= {Format(max)}",
        _ => "no bound",
    };

    public static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
