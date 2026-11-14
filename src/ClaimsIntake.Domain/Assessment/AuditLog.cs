using System.Collections.Concurrent;

namespace ClaimsIntake.Domain.Assessment;

/// <summary>
/// One tool call made while assessing a claim. Arguments are reduced to ids, dates, numbers and the length of
/// any free text: never a name, an address or the words of the claim (see ADR 0006).
/// </summary>
/// <param name="ClaimId">The claim the call was made for.</param>
/// <param name="Tool">The tool name, such as <c>search_policy_wording</c>.</param>
/// <param name="Arguments">The redacted arguments.</param>
/// <param name="ResultSummary">What came back, in a few words: counts and ids.</param>
/// <param name="DurationMs">How long the call took.</param>
/// <param name="Timestamp">When the call started.</param>
/// <param name="Succeeded">False when the tool threw. The summary then holds the exception type only.</param>
public sealed record AuditEntry(
    Guid ClaimId,
    string Tool,
    IReadOnlyDictionary<string, string> Arguments,
    string ResultSummary,
    double DurationMs,
    DateTimeOffset Timestamp,
    bool Succeeded = true);

/// <summary>Where the audit trail of tool calls is kept.</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>The entries for one claim, oldest first.</summary>
    Task<IReadOnlyList<AuditEntry>> ForClaimAsync(Guid claimId, CancellationToken cancellationToken);
}

/// <summary>Keeps the audit trail in memory. The local default; Azure uses Table Storage.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly ConcurrentDictionary<Guid, List<AuditEntry>> _entries = new();

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var list = _entries.GetOrAdd(entry.ClaimId, _ => []);
        lock (list)
        {
            list.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ForClaimAsync(Guid claimId, CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(claimId, out var list))
        {
            return Task.FromResult<IReadOnlyList<AuditEntry>>([]);
        }

        lock (list)
        {
            // OrderBy is stable, so calls that started in the same tick keep the order they were recorded in.
            return Task.FromResult<IReadOnlyList<AuditEntry>>([.. list.OrderBy(e => e.Timestamp)]);
        }
    }
}
