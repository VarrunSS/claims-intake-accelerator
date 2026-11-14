using System.Text.Json;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Telemetry;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;

namespace ClaimsIntake.Api.Intake;

public enum IntakeOutcome
{
    /// <summary>A new claim now waits for a handler.</summary>
    Accepted,

    /// <summary>The API already had a claim for this document. Nothing changed.</summary>
    Duplicate,

    /// <summary>The message was malformed or outside the limits. Retrying it will not help.</summary>
    Invalid,
}

public sealed record IntakeResult(IntakeOutcome Outcome, Claim? Claim = null, IReadOnlyList<string>? Problems = null);

/// <summary>
/// Accepts proposed claims from the intake pipeline, whether they arrive over HTTP (local mode) or from
/// Service Bus (Azure). Both routes end here, so both behave the same.
/// </summary>
public sealed class ClaimIntakeService(IClaimRepository repository, IAuditLog audit, ILogger<ClaimIntakeService> logger)
{
    public const int MaxFields = 20;
    public const int MaxIssues = 50;
    public const int MaxPages = 100;
    public const int MaxTotalCharacters = 2_000_000;
    public const int MaxValueLength = 4_000;
    public const int MaxDocumentIdLength = 300;
    public const int MaxAuditEntries = 100;
    public const int MaxFindings = 30;
    public const int MaxCitationsPerFinding = 10;

    public async Task<IntakeResult> AcceptAsync(ProposedClaim proposed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        var problems = Check(proposed);
        if (problems.Count > 0)
        {
            return new IntakeResult(IntakeOutcome.Invalid, Problems: problems);
        }

        // The claim id comes from the document id, so a redelivered message finds the claim it created.
        var existing = await repository.GetAsync(proposed.ClaimId, cancellationToken);
        if (existing is not null)
        {
            return Duplicate(existing);
        }

        var claim = proposed.ToClaim();
        try
        {
            await repository.AddAsync(claim, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Another delivery of the same document won the race.
            return Duplicate((await repository.GetAsync(claim.Id, cancellationToken))!);
        }

        // The tool calls made while the claim was assessed. Only a claim that was accepted gets its trail stored, so a
        // redelivered message does not repeat it. The entries are re-attributed to this claim.
        foreach (var entry in proposed.AuditTrail ?? [])
        {
            await audit.AppendAsync(entry with { ClaimId = claim.Id }, cancellationToken);
        }

        ApiMetrics.IntakeClaims.Add(1, new KeyValuePair<string, object?>("outcome", "accepted"));
        logger.LogInformation("Claim {ClaimId} accepted from document {DocumentId}: {Errors} errors, {Warnings} warnings",
            claim.Id, Sanitise(claim.SourceDocumentId),
            claim.Issues.Count(i => i.Severity == IssueSeverity.Error), claim.Issues.Count(i => i.Severity == IssueSeverity.Warning));
        return new IntakeResult(IntakeOutcome.Accepted, claim);
    }

    /// <summary>Parses a queue message body and accepts it. A body that is not a valid proposed claim is Invalid.</summary>
    public async Task<IntakeResult> AcceptJsonAsync(string json, CancellationToken cancellationToken)
    {
        ProposedClaim proposed;
        try
        {
            proposed = ProposedClaim.FromJson(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // The message is not logged: it holds claim text. The exception type is enough to diagnose.
            logger.LogWarning("A hand-off message could not be read: {ErrorType}", ex.GetType().Name);
            return new IntakeResult(IntakeOutcome.Invalid, Problems: ["The message is not a valid proposed claim."]);
        }

        return await AcceptAsync(proposed, cancellationToken);
    }

    /// <summary>Limits on the size and shape of a proposed claim. The sender is our pipeline, but it is not trusted to be well behaved.</summary>
    public static IReadOnlyList<string> Check(ProposedClaim proposed)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(proposed.DocumentId) || proposed.DocumentId.Length > MaxDocumentIdLength)
        {
            problems.Add($"documentId is required and must be at most {MaxDocumentIdLength} characters.");
        }

        if (proposed.Fields is null || proposed.Issues is null || proposed.SourcePages is null)
        {
            problems.Add("fields, issues and sourcePages are required.");
            return problems;
        }

        if (proposed.Fields.Count is 0 or > MaxFields
            || proposed.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != proposed.Fields.Count)
        {
            problems.Add($"fields must hold between 1 and {MaxFields} entries with distinct names.");
        }

        if (proposed.Fields.Any(f => f.Value is { Length: > MaxValueLength }))
        {
            problems.Add($"a field value is longer than {MaxValueLength} characters.");
        }

        if (proposed.Issues.Count > MaxIssues)
        {
            problems.Add($"issues must hold at most {MaxIssues} entries.");
        }

        if (proposed.SourcePages.Count > MaxPages || proposed.SourcePages.Sum(p => (long)p.Length) > MaxTotalCharacters)
        {
            problems.Add($"sourcePages must hold at most {MaxPages} pages and {MaxTotalCharacters} characters in all.");
        }

        if (proposed.AuditTrail is { } trail
            && (trail.Count > MaxAuditEntries
                || trail.Any(e => string.IsNullOrWhiteSpace(e.Tool) || e.Tool.Length > 80 || e.ResultSummary is not { Length: <= MaxValueLength }
                    || e.Arguments is null || e.Arguments.Count > 12 || e.Arguments.Any(a => a.Key.Length > 80 || a.Value is not { Length: <= 300 }))))
        {
            problems.Add($"auditTrail must hold at most {MaxAuditEntries} well-formed entries.");
        }

        if (proposed.Assessment is { } assessment
            && (assessment.Findings is null || assessment.Notes is null || assessment.Findings.Count > MaxFindings
                || assessment.Findings.Any(f => f.Citations is null || f.Citations.Count > MaxCitationsPerFinding
                    || f.Summary is not { Length: <= MaxValueLength } || f.Citations.Any(c => c.Text is not { Length: <= MaxValueLength }))))
        {
            problems.Add($"assessment must hold at most {MaxFindings} well-formed findings.");
        }

        return problems;
    }

    private static IntakeResult Duplicate(Claim existing)
    {
        ApiMetrics.IntakeClaims.Add(1, new KeyValuePair<string, object?>("outcome", "duplicate"));
        return new IntakeResult(IntakeOutcome.Duplicate, existing);
    }

    private static string Sanitise(string value)
    {
        var cleaned = new string([.. value.Select(c => char.IsControl(c) ? '_' : c)]);
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }
}
