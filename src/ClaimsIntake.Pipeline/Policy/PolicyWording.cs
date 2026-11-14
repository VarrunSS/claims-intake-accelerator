using System.Text.RegularExpressions;

namespace ClaimsIntake.Pipeline.Policy;

/// <summary>
/// One numbered clause of a policy wording: the unit that is indexed, searched and cited (ADR 0006).
/// </summary>
/// <param name="WordingId">The wording it belongs to, for example TM-PW-CC-2026.</param>
/// <param name="ClauseRef">The clause number, for example 4.1.</param>
/// <param name="Section">The name of the section it sits in, for example Exclusions.</param>
/// <param name="Heading">The clause heading, for example Flood.</param>
/// <param name="Text">The clause text, as written.</param>
public sealed record PolicyClause(string WordingId, string ClauseRef, string Section, string Heading, string Text)
{
    /// <summary>Identifies the clause across wordings, for example <c>TM-PW-CC-2026/4.1</c>.</summary>
    public string Key => $"{WordingId}/{ClauseRef}";
}

/// <summary>A policy wording split into clauses.</summary>
public sealed record PolicyWording(string Id, string Title, IReadOnlyList<PolicyClause> Clauses);

/// <summary>A clause returned by a search, with its score. Higher is better; scores from different searches are not comparable.</summary>
public sealed record ClauseHit(string WordingId, string ClauseRef, string Section, string Heading, string Text, double Score)
{
    public string Key => $"{WordingId}/{ClauseRef}";
}

/// <summary>Finds the clauses of the policy wordings that best match a query.</summary>
public interface IPolicyWordingSearch
{
    /// <param name="query">Words describing what to look for, for example the cause of a loss.</param>
    /// <param name="wordingId">Limit the search to one wording, or null to search them all.</param>
    /// <param name="top">The most clauses to return.</param>
    /// <returns>Clauses ranked best first. Empty when nothing matches.</returns>
    Task<IReadOnlyList<ClauseHit>> SearchAsync(string query, string? wordingId, int top, CancellationToken cancellationToken);
}

/// <summary>Checks that an id from a model or a request is shaped like a wording id before it is used in a query.</summary>
public static partial class WordingIds
{
    public static bool IsValid(string? id) => id is not null && ValidId().IsMatch(id);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidId();
}
