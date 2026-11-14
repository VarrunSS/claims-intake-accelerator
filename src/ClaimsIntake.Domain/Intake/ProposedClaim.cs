using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsIntake.Domain.Assessment;

namespace ClaimsIntake.Domain.Intake;

/// <summary>
/// What the intake pipeline hands to the review store: the extracted fields, the validation issues and the
/// text they were read from. It is the message on the Service Bus queue and the body of the local HTTP
/// hand-off. The agent only ever proposes; a handler decides (ADR 0002).
/// </summary>
/// <param name="DocumentId">Identifier of the source document (the blob name, or the file name locally).</param>
/// <param name="ReceivedAt">When intake received the document.</param>
/// <param name="Fields">The extracted fields.</param>
/// <param name="Issues">Data-quality issues found by <see cref="ClaimValidator"/>.</param>
/// <param name="SourcePages">The text read from the document, one entry per page.</param>
/// <param name="Assessment">Coverage findings against the policy wording, when the pipeline ran an assessor.</param>
/// <param name="AuditTrail">The tool calls made while assessing. The API stores them in its audit log.</param>
/// <param name="NotifiedOn">The date the claim was notified: the date the document states, otherwise the date it was received.
/// Null when the input-quality gate stopped the document before extraction.</param>
/// <param name="NotifiedOnSource"><c>document</c> when the document states the date, <c>received</c> when it comes from the received timestamp.</param>
public sealed record ProposedClaim(
    string DocumentId,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<ExtractedField> Fields,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<string> SourcePages,
    ClaimAssessment? Assessment = null,
    IReadOnlyList<AuditEntry>? AuditTrail = null,
    DateOnly? NotifiedOn = null,
    string? NotifiedOnSource = null)
{
    /// <summary>JSON settings shared by every producer and consumer of the hand-off message.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// A claim id derived from the document id, so the same document handed over twice (a redelivered
    /// message, a retried upload) is recognised as one claim.
    /// </summary>
    public Guid ClaimId => ClaimIds.ForDocument(DocumentId);

    public Claim ToClaim()
    {
        var claim = new Claim(ClaimId, DocumentId, ReceivedAt, Fields, Issues, SourcePages);
        if (Assessment is not null)
        {
            claim.AttachAssessment(Assessment);
        }

        return claim;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Applies every shared limit by cutting, never refusing (REL-04): the document text (see <see cref="WithinTextLimits"/>),
    /// the number of audit entries, and the length of each audit value. A cut is noted on the assessment.
    /// </summary>
    public ProposedClaim WithinLimits()
    {
        var result = WithinTextLimits();
        if (result.AuditTrail is not { } trail)
        {
            return result;
        }

        if (trail.Count <= IntakeLimits.MaxAuditEntries && trail.All(IsWithinLimits))
        {
            return result;
        }

        var kept = trail.Where(e => !string.IsNullOrWhiteSpace(e.Tool)).Take(IntakeLimits.MaxAuditEntries).Select(ClampEntry).ToList();
        var cut = trail.Count - kept.Count;
        var assessment = result.Assessment;
        if (cut > 0 && assessment is not null)
        {
            assessment = assessment with { Notes = [.. assessment.Notes, $"The audit trail was cut to {kept.Count} entries ({cut} not stored)."] };
        }

        return result with { AuditTrail = kept, Assessment = assessment };
    }

    private static bool IsWithinLimits(AuditEntry e) =>
        !string.IsNullOrWhiteSpace(e.Tool) && e.Tool.Length <= IntakeLimits.MaxAuditToolLength
        && e.ResultSummary is { Length: <= IntakeLimits.MaxValueLength }
        && e.Arguments is { Count: <= IntakeLimits.MaxAuditArguments }
        && e.Arguments.All(a => a.Key.Length <= IntakeLimits.MaxAuditToolLength && a.Value is { Length: <= IntakeLimits.MaxAuditArgumentLength });

    private static AuditEntry ClampEntry(AuditEntry e) => e with
    {
        Tool = Cut(e.Tool, IntakeLimits.MaxAuditToolLength),
        ResultSummary = Cut(e.ResultSummary ?? "", IntakeLimits.MaxValueLength),
        Arguments = (e.Arguments ?? new Dictionary<string, string>())
            .Take(IntakeLimits.MaxAuditArguments)
            .ToDictionary(a => Cut(a.Key, IntakeLimits.MaxAuditToolLength), a => Cut(a.Value ?? "", IntakeLimits.MaxAuditArgumentLength)),
    };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Cuts the document text to the shared page and character limits (<see cref="IntakeLimits"/>). If anything was cut, a
    /// blocking issue and a senior-review assessment tell the handler to read the original, so the claim is never lost to a limit.
    /// </summary>
    public ProposedClaim WithinTextLimits()
    {
        var total = SourcePages.Sum(p => (long)p.Length);
        if (SourcePages.Count <= IntakeLimits.MaxPages && total <= IntakeLimits.MaxTotalCharacters)
        {
            return this;
        }

        var pages = new List<string>();
        long budget = IntakeLimits.MaxTotalCharacters;
        foreach (var page in SourcePages.Take(IntakeLimits.MaxPages))
        {
            if (budget <= 0)
            {
                break;
            }

            var kept = page.Length <= budget ? page : page[..(int)budget];
            pages.Add(kept);
            budget -= kept.Length;
        }

        return CutDown(pages, "The document is longer than the intake limit, so only the first part of its text is shown.");
    }

    /// <summary>
    /// Makes the serialised message no larger than <paramref name="maxBytes"/> (default: below the Service Bus Standard
    /// 256 KB limit, REL-02). The document text is trimmed first, then the audit trail. The claim keeps its fields, issues,
    /// assessment and document id (the reference to the original), and gains a blocking issue that tells a person to read
    /// the original. Returns the same instance when it already fits.
    /// </summary>
    public ProposedClaim FitToSize(int maxBytes = IntakeLimits.MaxHandoffBytes)
    {
        if (Encoding.UTF8.GetByteCount(ToJson()) <= maxBytes)
        {
            return this;
        }

        var candidate = this;
        var pageChars = (int)Math.Min(SourcePages.Sum(p => (long)p.Length), int.MaxValue);
        for (var attempt = 0; attempt < 24; attempt++)
        {
            pageChars = (int)(pageChars * 0.6);
            candidate = CutDown(TrimPages(SourcePages, pageChars),
                "The document text was too long to hand over in full, so only the start of it is shown.");
            if (Encoding.UTF8.GetByteCount(candidate.ToJson()) <= maxBytes)
            {
                return candidate;
            }

            if (pageChars == 0)
            {
                break;
            }
        }

        // Still too large with no text: the audit trail is the only large part left.
        var trail = (candidate.AuditTrail ?? []).ToList();
        while (trail.Count > 0 && Encoding.UTF8.GetByteCount((candidate with { AuditTrail = trail }).ToJson()) > maxBytes)
        {
            trail.RemoveRange(trail.Count / 2, trail.Count - trail.Count / 2);
        }

        return candidate with { AuditTrail = trail };
    }

    private static List<string> TrimPages(IReadOnlyList<string> pages, int chars)
    {
        var result = new List<string>();
        var budget = chars;
        foreach (var page in pages)
        {
            if (budget <= 0)
            {
                break;
            }

            var kept = page.Length <= budget ? page : page[..budget];
            result.Add(kept);
            budget -= kept.Length;
        }

        return result;
    }

    private ProposedClaim CutDown(List<string> pages, string why)
    {
        var message = $"{why} Read the original document ({DocumentId}) before deciding.";
        var issues = Issues.Where(i => i.Code != IssueCode.DocumentTooLarge).ToList();
        issues.Add(new ValidationIssue(IssueCode.DocumentTooLarge, IssueSeverity.Error, null, message));
        var assessment = Assessment is null
            ? new ClaimAssessment("input-quality", [], ReviewRoute.SeniorReview, "Needs a person: the document text was cut to fit.", [message])
            : Assessment with
            {
                Route = ReviewRoute.SeniorReview,
                RouteReason = "Needs senior review: the document text was cut to fit.",
                Notes = [.. Assessment.Notes, message],
            };
        return this with { SourcePages = pages, Issues = issues, Assessment = assessment };
    }

    public static ProposedClaim FromJson(string json) =>
        JsonSerializer.Deserialize<ProposedClaim>(json, JsonOptions)
        ?? throw new JsonException("The hand-off message was empty.");
}

public static class ClaimIds
{
    /// <summary>A stable id (the first 16 bytes of the SHA-256 of the document id).</summary>
    public static Guid ForDocument(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(documentId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
