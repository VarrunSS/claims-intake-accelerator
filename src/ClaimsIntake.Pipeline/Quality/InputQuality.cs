using ClaimsIntake.Domain;
using ClaimsIntake.Pipeline.Text;

namespace ClaimsIntake.Pipeline.Quality;

/// <summary>When in the pipeline a check runs.</summary>
public enum QualityStage
{
    /// <summary>On the bytes, before any text is read: size, PDF structure, duplicate file.</summary>
    Document,

    /// <summary>On the text that was read: too little of it, not English, not about a claim.</summary>
    Text,
}

/// <summary>What a check found. The message is for the handler and never quotes the document.</summary>
/// <param name="Code">The validation issue code the handler will see.</param>
/// <param name="Check">The name of the check that raised it.</param>
/// <param name="Message">Plain English, with no document text in it.</param>
public sealed record QualityFinding(IssueCode Code, string Check, string Message);

/// <summary>What a check is given. <see cref="Text"/> is null in the <see cref="QualityStage.Document"/> stage.</summary>
public sealed record InputQualityContext(IntakeDocument Document, DocumentText? Text);

/// <summary>
/// A check on an incoming document, run before extraction. A finding does not fail the document: the pipeline hands
/// it to a person with the reason (ADR 0007), so an unreadable scan or a marketing email never disappears silently.
/// </summary>
public interface IInputQualityCheck
{
    string Name { get; }

    QualityStage Stage { get; }

    /// <returns>A finding, or null when the document passes this check.</returns>
    Task<QualityFinding?> CheckAsync(InputQualityContext context, CancellationToken cancellationToken);
}

/// <summary>Limits for the default checks.</summary>
public sealed class InputQualityOptions
{
    /// <summary>Largest document accepted, in bytes.</summary>
    public int MaxDocumentBytes { get; set; } = ClaimIntakePipeline.MaxDocumentBytes;

    /// <summary>The default for <see cref="MaxPages"/>: the pilot scope is 10 pages, so 50 leaves room without inviting a 5,000-page PDF.</summary>
    public const int DefaultMaxPages = 50;

    /// <summary>A PDF with more pages than this is handed to a person unread (INP-01). Must not exceed the shared limit of 100 pages.</summary>
    public int MaxPages { get; set; } = DefaultMaxPages;

    /// <summary>
    /// How long the checks and the text extraction may take for one document. Past it the document is handed to a person with a
    /// finding, rather than holding the Function. Set <see cref="TimeSpan.Zero"/> to turn the deadline off.
    /// </summary>
    public TimeSpan ProcessingDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fewer letters and digits than this and the text is treated as unreadable.</summary>
    public int MinTextCharacters { get; set; } = 40;

    /// <summary>Fewer distinct claim words than this and the text is treated as not being a claim.</summary>
    public int MinClaimTerms { get; set; } = 3;
}

/// <summary>Runs the checks of one stage in order and stops at the first finding.</summary>
public sealed class InputQualityGate(IEnumerable<IInputQualityCheck> checks)
{
    private readonly IInputQualityCheck[] _checks = [.. checks];

    public IReadOnlyList<string> CheckNames => [.. _checks.Select(c => c.Name)];

    public async Task<QualityFinding?> RunAsync(QualityStage stage, InputQualityContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var check in _checks.Where(c => c.Stage == stage))
        {
            if (await check.CheckAsync(context, cancellationToken) is { } finding)
            {
                return finding;
            }
        }

        return null;
    }

    /// <summary>The checks of Milestones 4 and 6 (six, with the file signature), with an in-memory record of the files seen.</summary>
    public static InputQualityGate CreateDefault(InputQualityOptions? options = null, IDocumentHashStore? hashes = null)
    {
        options ??= new InputQualityOptions();
        return new InputQualityGate(
        [
            new DocumentSizeCheck(options.MaxDocumentBytes),
            new FileSignatureCheck(),
            new PdfIntegrityCheck(options.MaxPages),
            new DuplicateFileCheck(hashes ?? new InMemoryDocumentHashStore()),
            new ReadableTextCheck(options.MinTextCharacters),
            new ClaimContentCheck(options.MinClaimTerms),
        ]);
    }
}
