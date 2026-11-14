using System.Globalization;

namespace ClaimsIntake.Domain.Assessment;

/// <summary>
/// Builds the audit entries for human decisions on a claim (SEC-04): approved, rejected, submitted and submit_failed. An entry
/// holds the actor's object id, the role they acted under, the route at the time, the outcome and the legacy reference. It
/// holds no name and no free text (a rejection records the length of the reason, not the reason).
/// The entries are keyed deterministically from the claim's own state, so writing one again is harmless.
/// </summary>
public static class DecisionAudit
{
    public const string Approved = "decision.approved";
    public const string Rejected = "decision.rejected";
    public const string Submitted = "decision.submitted";
    public const string SubmitFailed = "decision.submit_failed";

    public static AuditEntry ForApproved(Claim claim) =>
        Build(claim, Approved, claim.DecidedAt, "d-approved", "approved", extra: null);

    public static AuditEntry ForRejected(Claim claim) =>
        Build(claim, Rejected, claim.DecidedAt, "d-rejected", "rejected",
            new() { ["reason_length"] = (claim.RejectionReason?.Length ?? 0).ToString(CultureInfo.InvariantCulture) });

    public static AuditEntry ForSubmitted(Claim claim) =>
        Build(claim, Submitted, claim.SubmittedAt ?? claim.DecidedAt, "d-submitted", "submitted",
            new() { ["legacy_reference"] = claim.LegacyReference ?? "" });

    /// <param name="outcome"><c>refused</c> or <c>unavailable</c>.</param>
    /// <param name="at">When the attempt failed. Each failed attempt is its own entry.</param>
    public static AuditEntry ForSubmitFailed(Claim claim, string outcome, DateTimeOffset at) =>
        Build(claim, SubmitFailed, at, "d-submit_failed", outcome, extra: null, succeeded: false);

    private static AuditEntry Build(
        Claim claim, string tool, DateTimeOffset? at, string key, string outcome, Dictionary<string, string>? extra, bool succeeded = true)
    {
        var arguments = new Dictionary<string, string>
        {
            ["actor_oid"] = claim.DecidedBy ?? "",
            ["role"] = claim.DecidedAs ?? "",
            ["route"] = claim.DecidedOnRoute?.ToString() ?? "NoAssessment",
            ["outcome"] = outcome,
        };
        foreach (var (name, value) in extra ?? [])
        {
            arguments[name] = value;
        }

        return new AuditEntry(claim.Id, tool, arguments, outcome, 0, at ?? claim.DecidedAt ?? DateTimeOffset.UnixEpoch, succeeded, key);
    }
}
