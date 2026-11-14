using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Pipeline.Tools;

namespace ClaimsIntake.Pipeline.Assessment;

/// <summary>
/// What an assessor is given about one claim: the extracted fields and the dates it needs. The field values are
/// personal data. An assessor may read them and pass them to its tools, and must never log them.
/// </summary>
/// <param name="ClaimId">The id the claim will have in the review store.</param>
/// <param name="Fields">The extracted fields, by name.</param>
/// <param name="NotifiedOn">The date the claim reached the insurer (UK date).</param>
public sealed record AssessmentContext(Guid ClaimId, IReadOnlyDictionary<string, ExtractedField> Fields, DateOnly NotifiedOn)
{
    public string? PolicyNumber => Value(ClaimFields.PolicyNumber);

    public string? Description => Value(ClaimFields.LossDescription);

    public DateOnly? LossDate => ClaimValidator.TryParseDate(Value(ClaimFields.LossDate), out var d) ? d : null;

    public decimal? Amount => ClaimValidator.TryParseAmount(Value(ClaimFields.EstimatedAmount), out var a) ? a : null;

    private string? Value(string name) =>
        Fields.TryGetValue(name, out var f) && f.HasValue ? f.Value!.Trim() : null;
}

/// <summary>
/// Assesses a claim against its policy wording using the tools it is given, and says which clauses it relied on.
/// It only advises: a handler decides, and nothing here writes to any system (ADR 0002).
/// </summary>
public interface IClaimAssessor
{
    /// <summary>Short name recorded on the assessment: <c>rules</c> or <c>agent</c>.</summary>
    string Name { get; }

    Task<ClaimAssessment> AssessAsync(AssessmentContext context, ClaimTools tools, CancellationToken cancellationToken);
}
