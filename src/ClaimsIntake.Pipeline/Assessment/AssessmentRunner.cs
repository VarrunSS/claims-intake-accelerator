using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Policy;
using ClaimsIntake.Pipeline.Tools;
using Microsoft.Extensions.Logging;

namespace ClaimsIntake.Pipeline.Assessment;

/// <summary>An assessment and the tool calls that produced it.</summary>
public sealed record AssessmentOutcome(ClaimAssessment Assessment, IReadOnlyList<AuditEntry> AuditTrail);

/// <summary>
/// Runs the chosen <see cref="IClaimAssessor"/> for one claim with a fresh set of tools, and returns the assessment
/// with the audit trail of its tool calls. If the assessor fails, the claim is not lost: the outcome is a
/// senior-review assessment that says so, and the trail so far is kept.
/// </summary>
public sealed partial class AssessmentRunner(
    IClaimAssessor assessor,
    IPolicyLookup policies,
    IPolicyWordingSearch wording,
    IExistingClaims existingClaims,
    TimeProvider time,
    ILogger<AssessmentRunner> logger)
{
    public string AssessorName => assessor.Name;

    public async Task<AssessmentOutcome> RunAsync(AssessmentContext context, CancellationToken cancellationToken)
    {
        var tools = new ClaimTools(context.ClaimId, policies, wording, existingClaims, time);
        try
        {
            var assessment = await assessor.AssessAsync(context, tools, cancellationToken);
            return new AssessmentOutcome(assessment, tools.AuditTrail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only: messages from the model or the services can quote claim text.
            LogFailed(logger, context.ClaimId, assessor.Name, ex.GetType().Name);
            return new AssessmentOutcome(
                new ClaimAssessment(assessor.Name, [], ReviewRoute.SeniorReview,
                    "Needs senior review: the automatic assessment failed.",
                    [$"The automatic assessment failed ({ex.GetType().Name}) and was not completed."]),
                tools.AuditTrail);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Assessment of claim {ClaimId} by the {Assessor} assessor failed: {ErrorType}.")]
    private static partial void LogFailed(ILogger logger, Guid claimId, string assessor, string errorType);
}
