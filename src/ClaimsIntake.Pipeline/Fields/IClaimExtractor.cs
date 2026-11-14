using ClaimsIntake.Domain;
using ClaimsIntake.Pipeline.Text;

namespace ClaimsIntake.Pipeline.Fields;

/// <summary>
/// Turns document text into the claim fields, each with a confidence and the span it was read from.
/// Implementations return one entry for every name in <see cref="ClaimFields"/>; a field that was not found
/// has a null value and confidence 0. This is the seam ADR 0004 asks for: nothing outside this namespace
/// knows which model or framework sits behind it.
/// </summary>
public interface IClaimExtractor
{
    Task<IReadOnlyList<ExtractedField>> ExtractAsync(string documentId, DocumentText text, CancellationToken cancellationToken);
}

/// <summary>The extractor could not produce a usable result (for example, the model returned malformed JSON).</summary>
public sealed class ClaimExtractionException(string message, Exception? inner = null) : Exception(message, inner);

internal static class FieldNames
{
    public static readonly string[] All =
    [
        ClaimFields.PolicyNumber,
        ClaimFields.ClaimantName,
        ClaimFields.LossDate,
        ClaimFields.LossDescription,
        ClaimFields.EstimatedAmount,
    ];

    public static ExtractedField NotFound(string name) => new(name, null, 0.0);
}
