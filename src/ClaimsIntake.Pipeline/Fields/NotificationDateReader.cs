using System.Text.RegularExpressions;
using ClaimsIntake.Pipeline.Text;

namespace ClaimsIntake.Pipeline.Fields;

/// <summary>Where the notification date came from.</summary>
public static class NotificationDateSources
{
    /// <summary>The document states it: a "date notified" field, or the sent date of an email.</summary>
    public const string Document = "document";

    /// <summary>The document does not state it, so it is the time the document was received.</summary>
    public const string Received = "received";
}

/// <summary>
/// Reads the date a claim was notified from the document itself: a labelled field ("Date notified", "Date reported",
/// "Notified on") first, then the sent date of an email (the <c>Date:</c> or <c>Sent:</c> header). It reads text only, so
/// it is the same for the heuristic extractor and for a model. Dates are UK day-first, as in the extractor.
/// Nothing here looks at the clock: when the document says nothing, the caller falls back to the time the document
/// was received, so the same document gives the same answer however late it is processed (the Milestone 3 defect).
/// </summary>
public static class NotificationDateReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Labelled = new(
        @"(?:date\s+(?:notified|reported|of\s+notification|of\s+report|received)|notification\s+date|notified\s+on|reported\s+on)\s*[:\-]?\s*(?:[a-z]+day,?\s+)?(?<v>" + HeuristicClaimExtractor.Date + ")",
        Opts, Timeout);

    private static readonly Regex Header = new(
        @"^[ \t]*(?:date|sent)\s*:[ \t]*(?:[a-z]{3,9},?\s+)?(?<v>" + HeuristicClaimExtractor.Date + ")",
        Opts | RegexOptions.Multiline, Timeout);

    /// <summary>The notification date the document states, or null when it states none or states one that cannot be read.</summary>
    public static DateOnly? Read(DocumentText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var regex in new[] { Labelled, Header })
        {
            foreach (var page in text.Pages)
            {
                foreach (Match match in regex.Matches(page))
                {
                    var iso = HeuristicClaimExtractor.NormaliseDate(match.Groups["v"].Value, out _);
                    if (DateOnly.TryParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var date))
                    {
                        return date;
                    }
                }
            }
        }

        return null;
    }
}
