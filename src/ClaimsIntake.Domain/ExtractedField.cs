namespace ClaimsIntake.Domain;

/// <summary>
/// One value read from a claim document, with the model's confidence and where it came from.
/// A null or blank Value means the field was looked for and not found.
/// </summary>
/// <param name="Name">Field name. See <see cref="ClaimFields"/> for the names the rules use.</param>
/// <param name="Value">The extracted text, exactly as read from the document.</param>
/// <param name="Confidence">Confidence between 0 and 1 inclusive.</param>
/// <param name="Source">Where the value was found. Null when it was not found.</param>
public sealed record ExtractedField(string Name, string? Value, double Confidence, SourceSpan? Source = null)
{
    public string Name { get; } = !string.IsNullOrWhiteSpace(Name)
        ? Name
        : throw new ArgumentException("A field needs a name.", nameof(Name));

    public double Confidence { get; } = Confidence is >= 0.0 and <= 1.0
        ? Confidence
        : throw new ArgumentOutOfRangeException(nameof(Confidence), Confidence, "Confidence must be between 0 and 1.");

    public bool HasValue => !string.IsNullOrWhiteSpace(Value);
}

public static class ClaimConfidence
{
    /// <summary>
    /// The lowest confidence among the fields that have a value, or 0 when none has. A claim is only as
    /// trustworthy as its weakest field. A field that was looked for and not found does not count here;
    /// the validator reports required ones as issues.
    /// </summary>
    public static double Lowest(IEnumerable<ExtractedField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var found = fields.Where(f => f.HasValue).Select(f => f.Confidence).ToList();
        return found.Count == 0 ? 0.0 : found.Min();
    }
}
