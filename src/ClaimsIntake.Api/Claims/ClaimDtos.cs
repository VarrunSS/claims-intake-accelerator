using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;

namespace ClaimsIntake.Api.Claims;

public sealed record ClaimSummary(
    Guid Id,
    string SourceDocumentId,
    string? PolicyNumber,
    string? ClaimantName,
    ClaimStatus Status,
    DateTimeOffset ReceivedAt,
    int ErrorCount,
    int WarningCount,
    double MinConfidence,
    string? LegacyReference)
{
    public static ClaimSummary From(Claim claim) => new(
        claim.Id,
        claim.SourceDocumentId,
        claim.PolicyNumber,
        claim.ClaimantName,
        claim.Status,
        claim.ReceivedAt,
        claim.Issues.Count(i => i.Severity == IssueSeverity.Error),
        claim.Issues.Count(i => i.Severity == IssueSeverity.Warning),
        Math.Round(claim.MinConfidence, 2),
        claim.LegacyReference);
}

public sealed record FieldDto(string Name, string? Value, double Confidence, SourceSpan? Source);

public sealed record IssueDto(IssueCode Code, IssueSeverity Severity, string? Field, string Message);

public sealed record ClaimDetail(
    Guid Id,
    string SourceDocumentId,
    ClaimStatus Status,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<FieldDto> Fields,
    IReadOnlyList<IssueDto> Issues,
    IReadOnlyList<string> SourcePages,
    string? DecidedBy,
    string? DecidedByName,
    DateTimeOffset? DecidedAt,
    string? RejectionReason,
    string? LegacyReference,
    ClaimAssessment? Assessment = null)
{
    public static ClaimDetail From(Claim claim) => new(
        claim.Id,
        claim.SourceDocumentId,
        claim.Status,
        claim.ReceivedAt,
        [.. claim.Fields.Values.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => new FieldDto(f.Name, f.Value, f.Confidence, f.Source))],
        [.. claim.Issues.Select(i => new IssueDto(i.Code, i.Severity, i.Field, i.Message))],
        claim.SourcePages,
        claim.DecidedBy,
        claim.DecidedByName,
        claim.DecidedAt,
        claim.RejectionReason,
        claim.LegacyReference,
        claim.Assessment);
}

/// <summary>One tool call in the audit trail of a claim.</summary>
public sealed record AuditEntryDto(
    string Tool, IReadOnlyDictionary<string, string> Arguments, string ResultSummary, double DurationMs, DateTimeOffset Timestamp, bool Succeeded)
{
    public static AuditEntryDto From(AuditEntry e) => new(e.Tool, e.Arguments, e.ResultSummary, e.DurationMs, e.Timestamp, e.Succeeded);
}

/// <summary>What the duplicate check needs to know about a claim: no names, no text.</summary>
public sealed record ClaimHistoryItem(Guid Id, string? PolicyNumber, DateOnly? LossDate, decimal? AmountGbp, ClaimStatus Status)
{
    public static ClaimHistoryItem From(Claim claim)
    {
        var hasDate = ClaimValidator.TryParseDate(claim.Fields.GetValueOrDefault(ClaimFields.LossDate)?.Value, out var date);
        var hasAmount = ClaimValidator.TryParseAmount(claim.Fields.GetValueOrDefault(ClaimFields.EstimatedAmount)?.Value, out var amount);
        return new ClaimHistoryItem(claim.Id, claim.PolicyNumber, hasDate ? date : null, hasAmount ? amount : null, claim.Status);
    }
}

/// <summary>Approve takes no body. The one field is kept only to refuse old clients that still send a reviewer (SEC-02).</summary>
public sealed record ApproveRequest(string? Reviewer = null);

/// <summary>Reject takes a reason. <c>Reviewer</c> is kept only to refuse old clients that still send one (SEC-02).</summary>
public sealed record RejectRequest(string? Reason, string? Reviewer = null);

/// <summary>The signed-in caller, from the token.</summary>
public sealed record MeDto(string Id, string Name, IReadOnlyList<string> Roles, bool IsSenior);
