using System.Text.RegularExpressions;

namespace ClaimsIntake.Pipeline.Policy;

/// <summary>
/// Splits text into lower-case terms for BM25: letters and digits, common words removed, and a light suffix
/// stripper so that flood, floods, flooded and flooding are one term. It is not a full stemmer. It is small,
/// has no dependencies, and gives the same terms on every machine.
/// </summary>
public static partial class Bm25Tokeniser
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "any", "are", "as", "at", "be", "been", "but", "by", "for", "from", "has", "have", "if", "in", "into",
        "is", "it", "its", "may", "no", "not", "of", "on", "or", "our", "such", "than", "that", "the", "their", "them", "then",
        "there", "these", "this", "to", "was", "we", "were", "which", "who", "will", "with", "you", "your",
    };

    public static IReadOnlyList<string> Tokenise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var terms = new List<string>();
        foreach (Match match in Word().Matches(text.ToLowerInvariant()))
        {
            if (!StopWords.Contains(match.Value))
            {
                terms.Add(Stem(match.Value));
            }
        }

        return terms;
    }

    internal static string Stem(string word)
    {
        if (word.Length <= 3 || word.All(char.IsDigit))
        {
            return word;
        }

        // Plurals first, then the verb endings, so building and buildings meet at "build".
        var stem = word;
        if (stem.EndsWith("ies", StringComparison.Ordinal) && stem.Length > 4)
        {
            stem = stem[..^3] + "y";
        }
        else if (stem.EndsWith("sses", StringComparison.Ordinal))
        {
            stem = stem[..^2];
        }
        else if (stem.EndsWith("es", StringComparison.Ordinal) && stem.Length > 4)
        {
            stem = stem[..^1];
        }
        else if (stem.EndsWith('s') && !stem.EndsWith("ss", StringComparison.Ordinal))
        {
            stem = stem[..^1];
        }

        if (stem.EndsWith("ing", StringComparison.Ordinal) && stem.Length > 5)
        {
            stem = stem[..^3];
        }
        else if (stem.EndsWith("ed", StringComparison.Ordinal) && stem.Length > 4)
        {
            stem = stem[..^2];
        }

        // damage and damaged meet at "damag".
        return stem.Length > 3 && stem.EndsWith('e') ? stem[..^1] : stem;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}

/// <summary>
/// BM25 over policy clauses, written out in full because it is small (ADR 0006). Each clause is one document.
/// The heading counts twice, so a clause called "Flood" outranks a clause that only mentions flooding. Scores use
/// the statistics of the whole corpus even when a search is limited to one wording, so a score does not change
/// with the filter. The same query always gives the same ranking: ties break on wording id, then clause number.
/// </summary>
public sealed class Bm25PolicyWordingSearch : IPolicyWordingSearch
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int HeadingWeight = 2;

    private readonly List<Entry> _entries;
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    private sealed record Entry(PolicyClause Clause, Dictionary<string, int> TermCounts, int Length);

    public Bm25PolicyWordingSearch(IEnumerable<PolicyClause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        _entries = [.. clauses.Select(Index)];
        foreach (var term in _entries.SelectMany(e => e.TermCounts.Keys))
        {
            _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
        }

        _averageLength = _entries.Count == 0 ? 0 : _entries.Average(e => e.Length);
    }

    public Bm25PolicyWordingSearch(IEnumerable<PolicyWording> wordings)
        : this((wordings ?? throw new ArgumentNullException(nameof(wordings))).SelectMany(w => w.Clauses))
    {
    }

    public int ClauseCount => _entries.Count;

    public Task<IReadOnlyList<ClauseHit>> SearchAsync(string query, string? wordingId, int top, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        var terms = Bm25Tokeniser.Tokenise(query).Distinct(StringComparer.Ordinal).ToList();
        var hits = new List<ClauseHit>();

        foreach (var entry in _entries)
        {
            if (wordingId is not null && !entry.Clause.WordingId.Equals(wordingId, StringComparison.Ordinal))
            {
                continue;
            }

            var score = Score(entry, terms);
            if (score > 0)
            {
                var c = entry.Clause;
                hits.Add(new ClauseHit(c.WordingId, c.ClauseRef, c.Section, c.Heading, c.Text, Math.Round(score, 4)));
            }
        }

        IReadOnlyList<ClauseHit> ranked = [.. hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.WordingId, StringComparer.Ordinal)
            .ThenBy(h => h.ClauseRef, ClauseRefComparer.Instance)
            .Take(top)];
        return Task.FromResult(ranked);
    }

    private double Score(Entry entry, List<string> queryTerms)
    {
        var score = 0.0;
        foreach (var term in queryTerms)
        {
            if (!entry.TermCounts.TryGetValue(term, out var tf))
            {
                continue;
            }

            var n = _documentFrequency[term];
            var idf = Math.Log(1 + (_entries.Count - n + 0.5) / (n + 0.5));
            var norm = tf + K1 * (1 - B + B * entry.Length / _averageLength);
            score += idf * tf * (K1 + 1) / norm;
        }

        return score;
    }

    private static Entry Index(PolicyClause clause)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;

        void Add(IReadOnlyList<string> terms, int weight)
        {
            foreach (var term in terms)
            {
                counts[term] = counts.GetValueOrDefault(term) + weight;
                length += weight;
            }
        }

        Add(Bm25Tokeniser.Tokenise(clause.Heading), HeadingWeight);
        Add(Bm25Tokeniser.Tokenise(clause.Section), 1);
        Add(Bm25Tokeniser.Tokenise(clause.Text), 1);
        return new Entry(clause, counts, length);
    }
}

/// <summary>Orders clause numbers as people read them: 4.2 before 4.10.</summary>
public sealed class ClauseRefComparer : IComparer<string>
{
    public static readonly ClauseRefComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = (x ?? "").Split('.');
        var b = (y ?? "").Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var result = int.TryParse(a[i], out var ai) && int.TryParse(b[i], out var bi)
                ? ai.CompareTo(bi)
                : string.CompareOrdinal(a[i], b[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
