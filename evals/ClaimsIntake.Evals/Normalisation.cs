using System.Globalization;
using System.Text.RegularExpressions;
using ClaimsIntake.Domain;

namespace ClaimsIntake.Evals;

/// <summary>
/// How a predicted value and an expected value are compared. The rules are deliberately explicit and few, because a
/// loose comparison flatters the extractor and a strict one punishes harmless formatting. They are documented in
/// evals/README.md and each has a test.
/// </summary>
public static class Normalisation
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A date as <c>yyyy-MM-dd</c>. Reads ISO 8601 and the UK day-first formats ("3 November 2026", "03/11/2026"). Returns null
    /// for text that is not a date, so an unreadable date never equals an expected one.
    /// </summary>
    public static string? Date(string? value) =>
        ClaimValidator.TryParseDate(value, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// An amount in pence. Reads "£12,500", "8,750.00" and "12500". Returns null when it cannot be read, and never reads
    /// number words: "twelve thousand pounds" is a miss, not a match.
    /// </summary>
    public static long? Pence(string? value) =>
        ClaimValidator.TryParseAmount(value, out var amount) ? (long)decimal.Round(amount * 100m, MidpointRounding.AwayFromZero) : null;

    public static long Pence(decimal pounds) => (long)decimal.Round(pounds * 100m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A name for comparison: case-insensitive, "&amp;" equals "and", "Limited" equals "Ltd", punctuation dropped, spaces collapsed.
    /// Nothing cleverer: "Brindle &amp; Marsh" and "Brindle Marsh Joinery" are different names.
    /// </summary>
    public static string Name(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var text = value.Trim().ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal);
        text = Regex.Replace(text, @"[.,;:'’""]", "", RegexOptions.None, Timeout);
        text = Regex.Replace(text, @"\blimited\b", "ltd", RegexOptions.None, Timeout);
        return Regex.Replace(text, @"\s+", " ", RegexOptions.None, Timeout).Trim();
    }

    /// <summary>A policy number is exact after trimming: no case folding, no repairs. A near miss is wrong.</summary>
    public static string Policy(string? value) => value?.Trim() ?? "";

    /// <summary>Text with all whitespace collapsed, for comparing a quoted clause with the wording.</summary>
    public static string Whitespace(string? value) => Regex.Replace(value ?? "", @"\s+", " ", RegexOptions.None, Timeout).Trim();
}
