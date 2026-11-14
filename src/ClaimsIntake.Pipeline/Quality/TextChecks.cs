using System.Text.RegularExpressions;
using ClaimsIntake.Domain;

namespace ClaimsIntake.Pipeline.Quality;

/// <summary>
/// Too little text to be a claim: an empty page, or a scan that yielded a few stray characters. Counting letters and
/// digits (not whitespace or punctuation) is what separates a blank scan from a one-line message.
/// </summary>
public sealed class ReadableTextCheck(int minCharacters) : IInputQualityCheck
{
    public string Name => "readable_text";

    public QualityStage Stage => QualityStage.Text;

    public Task<QualityFinding?> CheckAsync(InputQualityContext context, CancellationToken cancellationToken)
    {
        var count = context.Text!.Pages.Sum(page => page.Count(char.IsLetterOrDigit));
        QualityFinding? finding = count >= minCharacters
            ? null
            : new(IssueCode.UnreadableDocument, Name,
                "Little or no text could be read from the document. It may be a scan or a photograph; a person needs to read it.");
        return Task.FromResult(finding);
    }
}

/// <summary>
/// Decides whether the text is a claim notification at all, using keywords only (no model, so it runs offline and costs
/// nothing). It counts the distinct claim words present. With enough of them the text passes. With few, it says why:
/// not English (few common English words), marketing (unsubscribe, offers), or simply nothing about a claim. It errs
/// towards sending a document to a person, which is the cheap mistake.
/// </summary>
public sealed class ClaimContentCheck(int minClaimTerms) : IInputQualityCheck
{
    private const int StrongEvidenceTerms = 4;
    private const int MinWordsForLanguageCheck = 15;
    private const double MinEnglishWordShare = 0.10;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private static readonly Regex Word = new(@"[\p{L}']+", RegexOptions.CultureInvariant, Timeout);

    private static readonly HashSet<string> EnglishStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "of", "to", "a", "in", "is", "was", "for", "on", "that", "it", "with", "as", "at", "by", "we", "our",
        "this", "have", "has", "are", "were", "be", "not", "from", "or", "an", "they", "their", "i", "you", "your", "please",
    };

    // One entry per idea, so "fire" and "fires" do not count twice.
    private static readonly Regex[] ClaimTerms =
    [
        Term(@"\bclaims?\b"), Term(@"\bpolic(?:y|ies)\b"), Term(@"\bTM-[A-Z]{2}-\d{4,8}\b"), Term(@"\b(?:loss|lost)\b"),
        Term(@"\bdamag\w*"), Term(@"\binsur\w*"), Term(@"\b(?:fire|smoke|burnt|blaze)\b"), Term(@"\bflood\w*"),
        Term(@"\b(?:theft|stolen|burglar\w*|break-?in|forced entry)\b"), Term(@"\b(?:burst|leak\w*|escape of water)\b"),
        Term(@"\b(?:storm|gale|hail)\b"), Term(@"\b(?:incident|occurred|happened)\b"), Term(@"\bexcess\b"),
        Term(@"\bpremises\b"), Term(@"£\s?\d"), Term(@"\b(?:date of loss|loss date)\b"), Term(@"\b(?:broke down|breakdown|failed|failure)\b"),
        Term(@"\b(?:notif\w*|report(?:ed|ing)?)\b"),
    ];

    private static readonly Regex[] MarketingMarkers =
    [
        Term(@"\bunsubscribe\b"), Term(@"\b(?:limited[- ]time|special offer|exclusive offer|offer ends)\b"), Term(@"\bclick here\b"),
        Term(@"\d+\s?% off\b"), Term(@"\b(?:discount|voucher|promo(?:tion)?)\b"), Term(@"\b(?:newsletter|webinar)\b"),
        Term(@"\b(?:free quote|get a quote|book now|buy now|sign up)\b"), Term(@"\bview (?:this email )?in (?:your )?browser\b"),
    ];

    public string Name => "claim_content";

    public QualityStage Stage => QualityStage.Text;

    public Task<QualityFinding?> CheckAsync(InputQualityContext context, CancellationToken cancellationToken)
    {
        var text = string.Join('\n', context.Text!.Pages);
        var claimTerms = ClaimTerms.Count(r => r.IsMatch(text));
        if (claimTerms >= StrongEvidenceTerms)
        {
            return Task.FromResult<QualityFinding?>(null);
        }

        var words = Word.Matches(text).Select(m => m.Value).ToList();
        if (words.Count >= MinWordsForLanguageCheck
            && words.Count(EnglishStopWords.Contains) / (double)words.Count < MinEnglishWordShare)
        {
            return Task.FromResult<QualityFinding?>(new(IssueCode.NotAClaim, Name,
                "The document does not appear to be written in English, so it was not processed. A person needs to read it."));
        }

        if (MarketingMarkers.Count(r => r.IsMatch(text)) >= 2)
        {
            return Task.FromResult<QualityFinding?>(new(IssueCode.NotAClaim, Name,
                "The document looks like marketing or a newsletter, not a claim notification."));
        }

        return Task.FromResult<QualityFinding?>(claimTerms >= minClaimTerms
            ? null
            : new(IssueCode.NotAClaim, Name,
                "The document does not look like a claim notification: it says little about a policy, a loss or damage."));
    }

    private static Regex Term(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
}
