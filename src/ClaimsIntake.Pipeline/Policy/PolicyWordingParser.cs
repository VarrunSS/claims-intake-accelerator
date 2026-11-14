using System.Text;
using System.Text.RegularExpressions;

namespace ClaimsIntake.Pipeline.Policy;

/// <summary>
/// Reads a policy wording written in markdown and chunks it by clause. A section is <c>## 4. Exclusions</c>; a
/// clause is <c>### 4.1 Flood</c> and the text under it up to the next heading. Text that is not under a clause
/// (the title, the synthetic-data notice) is not indexed. Chunking at the clause, not at a fixed size, keeps each
/// chunk one thing a handler can cite and read (ADR 0006).
/// </summary>
public static partial class PolicyWordingParser
{
    public static PolicyWording Parse(string markdown, string sourceName = "wording")
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var (meta, bodyStart) = ReadFrontMatter(lines, sourceName);

        var id = meta.GetValueOrDefault("id");
        if (!WordingIds.IsValid(id))
        {
            throw new InvalidDataException($"{sourceName}: the front matter needs an id of letters, digits, - and _.");
        }

        var title = meta.GetValueOrDefault("title") is { Length: > 0 } t ? t : id!;
        var clauses = new List<PolicyClause>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var section = "";
        string? clauseRef = null;
        string heading = "";
        var text = new StringBuilder();

        void Flush()
        {
            if (clauseRef is null)
            {
                return;
            }

            var body = text.ToString().Trim();
            if (body.Length == 0)
            {
                throw new InvalidDataException($"{sourceName}: clause {clauseRef} has no text.");
            }

            if (!seen.Add(clauseRef))
            {
                throw new InvalidDataException($"{sourceName}: clause {clauseRef} appears twice.");
            }

            clauses.Add(new PolicyClause(id!, clauseRef, section, heading, body));
            clauseRef = null;
            text.Clear();
        }

        for (var i = bodyStart; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (SectionLine().Match(line) is { Success: true } s)
            {
                Flush();
                section = s.Groups["name"].Value.Trim();
            }
            else if (ClauseLine().Match(line) is { Success: true } c)
            {
                Flush();
                clauseRef = c.Groups["ref"].Value;
                heading = c.Groups["heading"].Value.Trim();
            }
            else if (line.StartsWith('#'))
            {
                Flush(); // The document title: not part of any clause.
            }
            else if (clauseRef is not null)
            {
                text.AppendLine(line);
            }
        }

        Flush();
        return clauses.Count == 0
            ? throw new InvalidDataException($"{sourceName}: no clauses were found.")
            : new PolicyWording(id!, title, clauses);
    }

    /// <summary>Loads every wording in a folder (<c>*.md</c>, except README.md), ordered by id.</summary>
    public static IReadOnlyList<PolicyWording> LoadFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"The policy wordings folder \"{folder}\" does not exist.");
        }

        var wordings = Directory.EnumerateFiles(folder, "*.md")
            .Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
            .Select(f => Parse(File.ReadAllText(f, Encoding.UTF8), Path.GetFileName(f)))
            .OrderBy(w => w.Id, StringComparer.Ordinal)
            .ToList();

        return wordings.Count == 0
            ? throw new InvalidDataException($"The policy wordings folder \"{folder}\" holds no wordings.")
            : wordings.GroupBy(w => w.Id, StringComparer.Ordinal).Any(g => g.Count() > 1)
                ? throw new InvalidDataException("Two wordings share an id.")
                : wordings;
    }

    private static (Dictionary<string, string> Meta, int BodyStart) ReadFrontMatter(string[] lines, string sourceName)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            throw new InvalidDataException($"{sourceName}: the file must start with a front-matter block between --- lines.");
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                return (meta, i + 1);
            }

            var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                meta[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        throw new InvalidDataException($"{sourceName}: the front-matter block is not closed.");
    }

    [GeneratedRegex(@"^##\s+\d+\.?\s+(?<name>.+)$")]
    private static partial Regex SectionLine();

    [GeneratedRegex(@"^###\s+(?<ref>\d+(?:\.\d+)*)\s+(?<heading>.+)$")]
    private static partial Regex ClauseLine();
}
