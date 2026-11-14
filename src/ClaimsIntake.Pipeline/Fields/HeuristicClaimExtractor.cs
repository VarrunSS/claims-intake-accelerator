using System.Globalization;
using System.Text.RegularExpressions;
using ClaimsIntake.Domain;
using ClaimsIntake.Pipeline.Text;

namespace ClaimsIntake.Pipeline.Fields;

/// <summary>
/// Reads claim fields with regular expressions and keywords. It is deterministic, needs no network and no
/// credentials, and is the default so the whole pipeline runs on a laptop and in CI.
/// </summary>
/// <remarks>
/// Confidence is a statement about how the value was found, not a probability: a value next to its label
/// scores 0.90 to 0.97, a value found by pattern or keyword alone scores 0.40 to 0.88, and a guess scores
/// lowest. Ambiguity lowers it further (several different policy numbers, a day/month pair that could be
/// read either way). Anything under 0.80 is flagged to the handler by the validator.
/// </remarks>
public sealed class HeuristicClaimExtractor : IClaimExtractor
{
    private const string Date =
        @"(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4}|\d{1,2}(?:st|nd|rd|th)?\s+(?:of\s+)?" +
        @"(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sept?(?:ember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\.?,?\s+\d{4})";

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static Regex Rx(string pattern, RegexOptions extra = RegexOptions.None) => new(pattern, Opts | extra, Timeout);

    private static readonly Regex PolicyLabelled =
        Rx(@"\bpolic(?:y|ies)\s*(?:no\.?|number|num|ref(?:erence)?|#)?\s*[:#\-]?\s*(?<v>[A-Z]{2,4}-[A-Z]{2,4}-\d{4,8})\b");

    private static readonly Regex PolicyBare = Rx(@"\b(?<v>TM[\s\-]?CP[\s\-]?(?<n>\d{6}))\b");

    private static readonly Regex PolicyLoose =
        Rx(@"\bpolicy\s*(?:no\.?|number|num|ref(?:erence)?|#)\s*[:#\-]?\s*(?<v>[A-Z0-9][A-Z0-9/\-]{4,19})\b");

    private static readonly Regex DateLabelled =
        Rx(@"(?:date\s+of\s+(?:loss|incident|damage|theft|occurrence)|loss\s+date|incident\s+date)\s*[:\-]?\s*(?:[a-z]+day,?\s+)?(?<v>" + Date + ")");

    private static readonly Regex DateContextual =
        Rx(@"\b(?:occurred|happened|discovered|noticed|took\s+place|night\s+of|on\s+the\s+night)\b[^.\n]{0,40}?(?<v>" + Date + ")");

    private static readonly Regex DateAny = Rx(@"\b(?<v>" + Date + ")");

    private static readonly Regex ClaimantLabelled = Rx(
        @"^[ \t]*(?:insured(?:\s+name)?|name\s+of\s+(?:the\s+)?(?:insured|claimant|policyholder)|claimant(?:\s+name)?|policy\s*holder|company(?:\s+name)?|business(?:\s+name)?)\s*[:\-]\s*(?<v>[^\n]{2,80})",
        RegexOptions.Multiline);

    // Case-sensitive on purpose: it looks for capitalised words before a company suffix.
    private static readonly Regex ClaimantOrganisation = new(
        @"(?<v>(?:[A-Z][A-Za-z0-9'’.\-]*\s+(?:&\s+)?){1,5}(?:Ltd|Limited|LLP|PLC|Group|Practice)\b\.?)",
        RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex ClaimantFromHeader = Rx(@"^[ \t]*from\s*:\s*""?(?<v>[^<""\n]{2,60}?)""?\s*<", RegexOptions.Multiline);

    private static readonly Regex DescriptionLabelled = Rx(
        @"(?:description\s+of\s+(?:the\s+)?(?:loss|incident|claim|damage)|description|details\s+of\s+(?:the\s+)?(?:loss|incident)|what\s+happened|nature\s+of\s+(?:the\s+)?(?:loss|claim)|circumstances)" +
        @"\s*[:\-]\s*(?<v>\S[^\n]*(?:\n(?![ \t]*\n)(?![ \t]*[A-Za-z][A-Za-z /]{2,30}:)[^\n]*)*)");

    private static readonly Regex LossKeyword = Rx(
        @"\b(?:fire|flood(?:ed|ing)?|burst|leak(?:ed|ing)?|theft|stolen|burglar(?:y|ised)|broke\s+into|break-?in|damage[d]?|collision|vandal(?:ism|ised)|storm|smoke|crashed)\b");

    private static readonly Regex HeaderLine = Rx(@"^[ \t]*(?:from|to|cc|bcc|subject|date|sent)\s*:");

    private static readonly Regex AmountLabelled = Rx(
        @"(?:estimated\s+(?:amount|cost|loss|value|damage)|amount\s+claimed|claim\s+amount|claim\s+value|total\s+(?:loss|claim)|approx(?:imate|\.)?\s+(?:cost|value)|estimate)[^\d£\n]{0,25}(?<v>£\s?\d[\d,]*(?:\.\d{1,2})?)");

    private static readonly Regex AmountAny = Rx(@"(?<v>£\s?\d[\d,]*(?:\.\d{1,2})?)");

    private sealed record Hit(string Value, int Page, int Offset, int Length);

    public Task<IReadOnlyList<ExtractedField>> ExtractAsync(string documentId, DocumentText text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(text);
        var pages = text.Pages;

        IReadOnlyList<ExtractedField> fields =
        [
            Policy(documentId, pages),
            Claimant(documentId, pages),
            LossDate(documentId, pages),
            Description(documentId, pages),
            Amount(documentId, pages),
        ];
        return Task.FromResult(fields);
    }

    private static ExtractedField Policy(string doc, IReadOnlyList<string> pages)
    {
        var hit = First(pages, PolicyLabelled, "v");
        var confidence = 0.97;
        if (hit is null)
        {
            hit = First(pages, PolicyBare, "v", g => $"TM-CP-{g[^6..]}");
            confidence = 0.88;
        }

        if (hit is null)
        {
            hit = First(pages, PolicyLoose, "v", accept: (m, _) => m.Groups["v"].Value.Any(char.IsDigit));
            confidence = 0.55;
        }

        if (hit is null)
        {
            return FieldNames.NotFound(ClaimFields.PolicyNumber);
        }

        // Several different numbers in one document means we may have picked the wrong one.
        var distinct = pages.SelectMany(p => PolicyBare.Matches(p).Select(m => m.Groups["n"].Value)).Distinct().Count();
        if (distinct > 1)
        {
            confidence = Math.Min(confidence, 0.65);
        }

        return Field(doc, ClaimFields.PolicyNumber, hit, confidence);
    }

    private static ExtractedField Claimant(string doc, IReadOnlyList<string> pages)
    {
        var hit = First(pages, ClaimantLabelled, "v", g => g.Trim().TrimEnd('.', ',', ';'));
        if (hit is not null)
        {
            return Field(doc, ClaimFields.ClaimantName, hit, 0.93);
        }

        hit = First(pages, ClaimantOrganisation, "v", g => g.Trim());
        if (hit is not null)
        {
            return Field(doc, ClaimFields.ClaimantName, hit, 0.62);
        }

        // The sender of an email is often a broker, not the insured, so this is a weak guess.
        hit = First(pages, ClaimantFromHeader, "v", g => g.Trim());
        return hit is null
            ? FieldNames.NotFound(ClaimFields.ClaimantName)
            : Field(doc, ClaimFields.ClaimantName, hit, 0.45);
    }

    private static ExtractedField LossDate(string doc, IReadOnlyList<string> pages)
    {
        var confidence = 0.93;
        var hit = First(pages, DateLabelled, "v");
        if (hit is null)
        {
            confidence = 0.72;
            hit = First(pages, DateContextual, "v");
        }

        if (hit is null)
        {
            confidence = 0.40;
            // An email's own Date: header is when it was sent, not when the loss happened.
            hit = First(pages, DateAny, "v", accept: (m, page) => !OnHeaderLine(m.Index, page));
        }

        if (hit is null)
        {
            return FieldNames.NotFound(ClaimFields.LossDate);
        }

        var normalised = NormaliseDate(hit.Value, out var ambiguous);
        if (ambiguous)
        {
            confidence = Math.Max(0.0, confidence - 0.08); // 03/04/2026 could be read either way.
        }

        return Field(doc, ClaimFields.LossDate, hit with { Value = normalised }, confidence);
    }

    private static ExtractedField Description(string doc, IReadOnlyList<string> pages)
    {
        var hit = First(pages, DescriptionLabelled, "v", g => Collapse(g));
        if (hit is not null)
        {
            return Field(doc, ClaimFields.LossDescription, hit, 0.88);
        }

        for (var p = 0; p < pages.Count; p++)
        {
            foreach (Match match in LossKeyword.Matches(pages[p]))
            {
                if (OnHeaderLine(match.Index, pages[p]))
                {
                    continue;
                }

                var (start, length) = SentenceAround(pages[p], match.Index);
                var sentence = Collapse(pages[p].Substring(start, length));
                if (sentence.Length >= 10)
                {
                    return Field(doc, ClaimFields.LossDescription, new Hit(sentence, p + 1, start, length), 0.55);
                }
            }
        }

        return FieldNames.NotFound(ClaimFields.LossDescription);
    }

    private static ExtractedField Amount(string doc, IReadOnlyList<string> pages)
    {
        var hit = First(pages, AmountLabelled, "v", g => g.Trim());
        if (hit is not null)
        {
            return Field(doc, ClaimFields.EstimatedAmount, hit, 0.90);
        }

        // A bare pound amount may be the excess or a limit rather than the loss, so it scores low.
        hit = First(pages, AmountAny, "v", g => g.Trim(), accept: (m, page) => !Preceded(m.Index, page, "excess"));
        return hit is null
            ? FieldNames.NotFound(ClaimFields.EstimatedAmount)
            : Field(doc, ClaimFields.EstimatedAmount, hit, 0.55);
    }

    private static Hit? First(
        IReadOnlyList<string> pages,
        Regex regex,
        string group,
        Func<string, string>? map = null,
        Func<Match, string, bool>? accept = null)
    {
        for (var p = 0; p < pages.Count; p++)
        {
            foreach (Match match in regex.Matches(pages[p]))
            {
                var g = match.Groups[group];
                if (!g.Success || (accept is not null && !accept(match, pages[p])))
                {
                    continue;
                }

                // The span covers the text as it appears in the document; the value may be tidied.
                return new Hit(map is null ? g.Value : map(g.Value), p + 1, g.Index, g.Length);
            }
        }

        return null;
    }

    private static ExtractedField Field(string doc, string name, Hit hit, double confidence) =>
        new(name, hit.Value, Math.Round(confidence, 2), new SourceSpan(doc, hit.Page, hit.Offset, hit.Length));

    private static bool OnHeaderLine(int index, string page)
    {
        var lineStart = index == 0 ? 0 : page.LastIndexOf('\n', index - 1) + 1;
        var lineEnd = page.IndexOf('\n', index);
        var line = page[lineStart..(lineEnd < 0 ? page.Length : lineEnd)];
        return HeaderLine.IsMatch(line);
    }

    private static bool Preceded(int index, string page, string word)
    {
        var from = Math.Max(0, index - 25);
        return page.AsSpan(from, index - from).Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Start, int Length) SentenceAround(string page, int index)
    {
        var start = index;
        while (start > 0 && page[start - 1] is not ('.' or '!' or '?' or '\n'))
        {
            start--;
        }

        var end = index;
        while (end < page.Length && page[end] is not ('.' or '!' or '?' or '\n'))
        {
            end++;
        }

        if (end < page.Length && page[end] is '.' or '!' or '?')
        {
            end++;
        }

        while (start < end && char.IsWhiteSpace(page[start]))
        {
            start++;
        }

        return (start, end - start);
    }

    private static string Collapse(string value) => Regex.Replace(value, @"\s+", " ", RegexOptions.None, Timeout).Trim();

    /// <summary>
    /// Converts a date to yyyy-MM-dd when it can be read, assuming UK day-first order. A date that cannot be
    /// read (31/02/2026) is returned as written so the validator reports it. ambiguous is true for a
    /// numeric date whose day and month are both 12 or less and differ.
    /// </summary>
    internal static string NormaliseDate(string raw, out bool ambiguous)
    {
        ambiguous = false;
        var text = raw.Trim();

        if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$", RegexOptions.None, Timeout))
        {
            return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? text : raw;
        }

        if (text.Contains('/', StringComparison.Ordinal))
        {
            var parts = text.Split('/');
            if (parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                && year is >= 1900 and <= 2200 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            {
                ambiguous = day <= 12 && month <= 12 && day != month;
                return new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return raw;
        }

        var cleaned = Regex.Replace(text, @"(?<=\d)(?:st|nd|rd|th)\b", "", Opts, Timeout);
        cleaned = Regex.Replace(cleaned, @"\bof\b|[,.]", " ", Opts, Timeout);
        cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.None, Timeout).Trim()
            .Replace("Sept ", "Sep ", StringComparison.OrdinalIgnoreCase);
        return DateOnly.TryParseExact(cleaned, ["d MMMM yyyy", "d MMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : raw;
    }
}
