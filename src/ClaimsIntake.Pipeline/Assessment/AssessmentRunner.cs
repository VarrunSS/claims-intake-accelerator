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
    ILogger<AssessmentRunner> logger,
    IClaimAssessor? routeFloor = null)
{
    public string AssessorName => assessor.Name;

    /// <summary>True when a deterministic rules check runs beside the assessor as a route floor.</summary>
    public bool HasRouteFloor => routeFloor is not null;

    public async Task<AssessmentOutcome> RunAsync(AssessmentContext context, CancellationToken cancellationToken)
    {
        // The tools are bound to this claim's own policy and wording by the code, from the pipeline's lookup (AI-01, AI-06).
        var binding = new ToolBinding(context.PolicyNumber, context.Policy?.WordingId);
        var tools = new ClaimTools(context.ClaimId, policies, wording, existingClaims, time, binding);
        try
        {
            var assessment = await assessor.AssessAsync(context, tools, cancellationToken);
            var trail = tools.AuditTrail;
            if (routeFloor is not null)
            {
                // The deterministic floor (AI-02): the rules assessor runs beside the agent with its own tools, and the agent can
                // raise the route but never lower it. Its calls are kept in the trail, after the agent's.
                var floorTools = new ClaimTools(context.ClaimId, policies, wording, existingClaims, time, binding);
                var floor = await RunFloorAsync(routeFloor, context, floorTools, cancellationToken);
                assessment = RouteFloor.Apply(assessment, floor);
                trail = [.. trail, .. floorTools.AuditTrail];
            }

            return new AssessmentOutcome(assessment, trail);
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

    private async Task<ClaimAssessment?> RunFloorAsync(
        IClaimAssessor floorAssessor, AssessmentContext context, ClaimTools floorTools, CancellationToken cancellationToken)
    {
        try
        {
            return await floorAssessor.AssessAsync(context, floorTools, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, context.ClaimId, floorAssessor.Name, ex.GetType().Name);
            return null; // RouteFloor.Apply treats a missing floor as "could not check", which is senior review.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Assessment of claim {ClaimId} by the {Assessor} assessor failed: {ErrorType}.")]
    private static partial void LogFailed(ILogger logger, Guid claimId, string assessor, string errorType);
}
