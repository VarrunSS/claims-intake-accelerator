using System.Diagnostics.Metrics;
using ClaimsIntake.Domain.Assessment;

namespace ClaimsIntake.Pipeline.Assessment;

/// <summary>
/// The deterministic route floor (AI-02). The citation guard can only remove things from a model's answer, but the attack that
/// matters for a claims tool is omission: a document that talks the model into leaving out an exclusion. So the rules assessor,
/// which is offline, free and does not read instructions, runs beside the agent. The final route is the more cautious of the two:
/// the agent can raise the route, never lower it. Findings the rules check made and the agent did not are added, so the handler
/// sees them, and a note says the two disagreed.
/// </summary>
public static class RouteFloor
{
    public const string MeterName = "ClaimsIntake.Pipeline";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>How often the rules check raised the route over the agent's. A rise in this is worth a look: a model, a prompt or a document type has changed.</summary>
    public static readonly Counter<long> Raised =
        Meter.CreateCounter<long>("claims.assessment.route_floor_raised", unit: "{claim}",
            description: "Claims the deterministic rules check sent to senior review when the agent had not.");

    /// <param name="agent">What the agent assessor produced, after the citation guard.</param>
    /// <param name="floor">What the rules assessor produced, or null when it could not run (treated as "unchecked", so senior review).</param>
    public static ClaimAssessment Apply(ClaimAssessment agent, ClaimAssessment? floor)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (floor is null)
        {
            return Raise(agent, [], "The rules check could not be completed, so the route was raised to senior review.",
                "Needs senior review: the rules check could not be completed.");
        }

        var findings = agent.Findings.ToList();
        var added = new List<string>();
        foreach (var candidate in floor.Findings.Where(f => f.Kind != FindingKind.CoverageClause))
        {
            if (!findings.Any(f => Same(f, candidate)))
            {
                findings.Add(candidate);
                added.Add(Describe(candidate.Kind));
            }
        }

        var raises = floor.Route == ReviewRoute.SeniorReview && agent.Route == ReviewRoute.StraightToHandler;
        if (!raises && added.Count == 0)
        {
            return agent;
        }

        var what = added.Count > 0 ? string.Join(", ", added.Distinct()) : "a reason for senior review";
        var note = $"The rules check found {what} that the assessment did not report.";
        if (raises)
        {
            note += " The route was raised to senior review.";
            Raised.Add(1);
            ModelRunMetrics.FloorRaised();
        }

        return agent with
        {
            Findings = findings,
            Route = raises ? ReviewRoute.SeniorReview : agent.Route,
            RouteReason = raises ? $"Needs senior review: the rules check found {what}." : agent.RouteReason,
            Notes = [.. agent.Notes, note],
        };
    }

    private static ClaimAssessment Raise(ClaimAssessment agent, List<CoverageFinding> extra, string note, string reason)
    {
        if (agent.Route == ReviewRoute.SeniorReview)
        {
            return agent with { Notes = [.. agent.Notes, note] };
        }

        Raised.Add(1);
        ModelRunMetrics.FloorRaised();
        return agent with { Findings = [.. agent.Findings, .. extra], Route = ReviewRoute.SeniorReview, RouteReason = reason, Notes = [.. agent.Notes, note] };
    }

    private static bool Same(CoverageFinding a, CoverageFinding b) =>
        a.Kind == b.Kind
        && (a.Kind == FindingKind.PossibleDuplicate
            ? string.Equals(a.RelatedClaimRef, b.RelatedClaimRef, StringComparison.Ordinal)
            : a.Citations.Any(x => b.Citations.Any(y => x.WordingId == y.WordingId && x.ClauseRef == y.ClauseRef)) || a.Citations.Count == 0 && b.Citations.Count == 0);

    private static string Describe(FindingKind kind) => kind switch
    {
        FindingKind.PossibleExclusion => "a possible exclusion",
        FindingKind.NotificationBreach => "a late notification",
        FindingKind.PossibleDuplicate => "a possible duplicate",
        _ => "a finding",
    };
}
