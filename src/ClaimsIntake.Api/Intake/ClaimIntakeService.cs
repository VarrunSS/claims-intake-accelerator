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
    public async Task<IntakeResult> AcceptAsync(ProposedClaim proposed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        var problems = Check(proposed);
        if (problems.Count > 0)
        {
            return new IntakeResult(IntakeOutcome.Invalid, Problems: problems);
        }

        // Over-long text and trails are cut down with a note and a blocking issue, never refused (REL-04): a claim that
        // cannot be read by a person is the failure to avoid, not one that was cut to fit.
        proposed = proposed.WithinLimits();

        // The claim id comes from the document id, so a redelivered message finds the claim it created.
        var existing = await repository.GetAsync(proposed.ClaimId, cancellationToken);
        if (existing is not null)
        {
            return Duplicate(existing);
        }

        var claim = proposed.ToClaim();

        // The tool calls made while the claim was assessed, written BEFORE the claim (REL-05). Each row has a deterministic
        // key (its tick and its position in the trail) and is upserted, so if a write fails half way the message is
        // redelivered and the whole trail is written again with no duplicates and nothing missing. Only then is the claim added.
        var trail = proposed.AuditTrail ?? [];
        for (var i = 0; i < trail.Count; i++)
        {
            await audit.AppendAsync(trail[i] with { ClaimId = claim.Id, Key = $"t{i:D3}" }, cancellationToken);
        }

        try
        {
            await repository.AddAsync(claim, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Another delivery of the same document won the race.
            return Duplicate((await repository.GetAsync(claim.Id, cancellationToken))!);
        }

        ApiMetrics.IntakeClaims.Add(1, new KeyValuePair<string, object?>("outcome", "accepted"));

        // The claim id, never the document id: a blob name can be a person's name (PII-01).
        logger.LogInformation("Claim {ClaimId} accepted: {Errors} errors, {Warnings} warnings",
            claim.Id,
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

        if (string.IsNullOrWhiteSpace(proposed.DocumentId) || proposed.DocumentId.Length > IntakeLimits.MaxDocumentIdLength)
        {
            problems.Add($"documentId is required and must be at most {IntakeLimits.MaxDocumentIdLength} characters.");
        }

        if (proposed.Fields is null || proposed.Issues is null || proposed.SourcePages is null)
        {
            problems.Add("fields, issues and sourcePages are required.");
            return problems;
        }

        if (proposed.Fields.Count is 0 or > IntakeLimits.MaxFields
            || proposed.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != proposed.Fields.Count)
        {
            problems.Add($"fields must hold between 1 and {IntakeLimits.MaxFields} entries with distinct names.");
        }

        if (proposed.Fields.Any(f => f.Value is { Length: > IntakeLimits.MaxValueLength }))
        {
            problems.Add($"a field value is longer than {IntakeLimits.MaxValueLength} characters.");
        }

        if (proposed.Issues.Count > IntakeLimits.MaxIssues)
        {
            problems.Add($"issues must hold at most {IntakeLimits.MaxIssues} entries.");
        }

        if (proposed.Assessment is { } assessment
            && (assessment.Findings is null || assessment.Notes is null || assessment.Findings.Count > IntakeLimits.MaxFindings
                || assessment.Findings.Any(f => f.Citations is null || f.Citations.Count > IntakeLimits.MaxCitationsPerFinding
                    || f.Summary is not { Length: <= IntakeLimits.MaxValueLength } || f.Citations.Any(c => c.Text is not { Length: <= IntakeLimits.MaxValueLength }))))
        {
            problems.Add($"assessment must hold at most {IntakeLimits.MaxFindings} well-formed findings.");
        }

        return problems;
    }

    private static IntakeResult Duplicate(Claim existing)
    {
        ApiMetrics.IntakeClaims.Add(1, new KeyValuePair<string, object?>("outcome", "duplicate"));
        return new IntakeResult(IntakeOutcome.Duplicate, existing);
    }
}
