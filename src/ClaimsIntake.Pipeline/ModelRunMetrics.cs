namespace ClaimsIntake.Pipeline;

/// <summary>
/// Counts what went wrong with a model's output while one claim is processed. An evaluation run sets
/// <see cref="Current"/> around each claim (it flows with the async call chain); in production nothing sets it and the
/// calls here do nothing. It holds counts only, never text.
/// </summary>
public sealed class ModelRunMetrics
{
    private static readonly AsyncLocal<ModelRunMetrics?> CurrentValue = new();
    private int _malformed;
    private int _schemaViolations;
    private int _guardRemovals;
    private int _floorRaised;

    public static ModelRunMetrics? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }

    /// <summary>Replies that were empty or not JSON at all.</summary>
    public int MalformedOutputs => Volatile.Read(ref _malformed);

    /// <summary>Replies that were JSON but not the shape the schema asks for (a missing or mistyped field).</summary>
    public int SchemaViolations => Volatile.Read(ref _schemaViolations);

    /// <summary>Citations and findings the citation guard removed.</summary>
    public int GuardRemovals => Volatile.Read(ref _guardRemovals);

    /// <summary>Claims the deterministic route floor sent to senior review when the agent had not.</summary>
    public int FloorRaises => Volatile.Read(ref _floorRaised);

    public static void FloorRaised() => Current?.AddFloorRaised();

    public static void Malformed() => Current?.AddMalformed();

    public static void SchemaViolation() => Current?.AddSchemaViolation();

    public static void GuardRemoved(int count) => Current?.AddGuardRemovals(count);

    private void AddFloorRaised() => Interlocked.Increment(ref _floorRaised);

    private void AddMalformed() => Interlocked.Increment(ref _malformed);

    private void AddSchemaViolation() => Interlocked.Increment(ref _schemaViolations);

    private void AddGuardRemovals(int count) => Interlocked.Add(ref _guardRemovals, count);
}
