namespace ClaimsIntake.Domain.Security;

/// <summary>The app roles the system uses (ADR 0009). Human roles come from user tokens, service roles from app-only tokens.</summary>
public static class AppRoles
{
    /// <summary>May read the review queue, reject any claim and approve a claim routed to a handler.</summary>
    public const string ClaimsHandler = "ClaimsHandler";

    /// <summary>Everything a claims handler may do, and approve a claim routed to senior review.</summary>
    public const string SeniorHandler = "SeniorHandler";

    /// <summary>App-only: the intake pipeline's identity reads the review store's claim history.</summary>
    public const string IntakeReadHistory = "Intake.ReadHistory";

    /// <summary>App-only: the local HTTP hand-off from the inbox watcher (development only).</summary>
    public const string IntakeSubmit = "Intake.Submit";

    /// <summary>App-only, on the legacy claims system: create a claim. Granted to the review API's identity only.</summary>
    public const string ClaimsWrite = "Claims.Write";

    /// <summary>App-only, on the legacy claims system: read claims for the duplicate check.</summary>
    public const string ClaimsRead = "Claims.Read";

    /// <summary>App-only, on the legacy claims system: read policies.</summary>
    public const string PoliciesRead = "Policies.Read";
}

/// <summary>
/// The person making a decision, as the signed-in token describes them. The id is the token's object id (<c>oid</c>), the
/// value that is stored and audited. The display name is for screens only and is never used to decide anything.
/// </summary>
public sealed record Approver
{
    public Approver(string id, string? displayName, IEnumerable<string> roles)
    {
        Id = !string.IsNullOrWhiteSpace(id) ? id.Trim() : throw new ArgumentException("The approver must be identified.", nameof(id));
        var name = new string([.. (displayName ?? "").Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        DisplayName = name.Length == 0 ? null : name.Length <= 200 ? name : name[..200];
        Roles = [.. (roles ?? throw new ArgumentNullException(nameof(roles))).Distinct(StringComparer.Ordinal)];
    }

    public string Id { get; }
    public string? DisplayName { get; }
    public IReadOnlyList<string> Roles { get; }

    public bool IsSenior => Roles.Contains(AppRoles.SeniorHandler);
    public bool IsHandler => IsSenior || Roles.Contains(AppRoles.ClaimsHandler);

    /// <summary>The role this person acts under when deciding: the senior role wins when both are held.</summary>
    public string ActingRole => IsSenior ? AppRoles.SeniorHandler : AppRoles.ClaimsHandler;
}

/// <summary>The approver lacks the role the claim's route requires. Mapped to HTTP 403.</summary>
public sealed class InsufficientRoleException(string message) : InvalidOperationException(message);
