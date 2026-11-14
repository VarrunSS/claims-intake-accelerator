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
/// <param name="Key">A deterministic discriminator for the entry within its claim and tick, such as <c>t003</c> for the fourth
/// tool call or <c>d-approved</c> for a decision. Writing the same entry twice (a redelivered message, a retried approval)
/// replaces it instead of adding a second. Null means the key is derived from the entry's content.</param>
public sealed record AuditEntry(
    Guid ClaimId,
    string Tool,
    IReadOnlyDictionary<string, string> Arguments,
    string ResultSummary,
    double DurationMs,
    DateTimeOffset Timestamp,
    bool Succeeded = true,
    string? Key = null)
{
    /// <summary>The row key: ticks first so entries sort by time, then the discriminator. Same entry, same key, always.</summary>
    public string RowKey => $"{Timestamp.UtcTicks:D19}-{Key ?? ContentKey()}";

    private string ContentKey()
    {
        var text = $"{Tool}|{string.Join(",", Arguments.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}={a.Value}"))}|{ResultSummary}|{Succeeded}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
    }
}

/// <summary>Where the audit trail of a claim is kept: its tool calls and the human decisions. Appending an entry that has the same row key replaces it.</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>The entries for one claim, oldest first.</summary>
    Task<IReadOnlyList<AuditEntry>> ForClaimAsync(Guid claimId, CancellationToken cancellationToken);
}

/// <summary>Keeps the audit trail in memory. The local default; Azure uses Table Storage.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    // Keyed by row key, so writing the same entry again replaces it (an upsert), as the Table Storage log does.
    private readonly ConcurrentDictionary<Guid, SortedDictionary<string, AuditEntry>> _entries = new();

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var rows = _entries.GetOrAdd(entry.ClaimId, _ => new SortedDictionary<string, AuditEntry>(StringComparer.Ordinal));
        lock (rows)
        {
            rows[entry.RowKey] = entry;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ForClaimAsync(Guid claimId, CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(claimId, out var rows))
        {
            return Task.FromResult<IReadOnlyList<AuditEntry>>([]);
        }

        lock (rows)
        {
            return Task.FromResult<IReadOnlyList<AuditEntry>>([.. rows.Values]);
        }
    }
}
