using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Pipeline.Assessment;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Prompts;
using ClaimsIntake.Pipeline.Quality;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.Logging;

namespace ClaimsIntake.Pipeline;

/// <summary>What the pipeline produced for one document.</summary>
public sealed record PipelineResult(ProposedClaim Claim, double Confidence)
{
    public int ErrorCount => Claim.Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int WarningCount => Claim.Issues.Count(i => i.Severity == IssueSeverity.Warning);
}

/// <summary>
/// The intake logic, free of any host: check the input, extract text, extract fields, look up the policy, validate,
/// assess against the policy wording (when an assessor is configured), hand off.
/// A document that fails an input-quality check is handed off too, as a claim with no fields and one blocking issue that
/// says why, so a person sees it (ADR 0007).
/// The Azure Function, the local inbox watcher and the tests all call this class. It only proposes a claim;
/// the write to the legacy system happens after a handler approves (ADR 0002).
/// </summary>
public sealed partial class ClaimIntakePipeline(
    IDocumentTextExtractor textExtractor,
    IClaimExtractor claimExtractor,
    IPolicyLookup policyLookup,
    IClaimSink sink,
    TimeProvider time,
    ILogger<ClaimIntakePipeline> logger,
    AssessmentRunner? assessment = null,
    InputQualityGate? quality = null,
    IPromptAttackDetector? detector = null)
{
    private readonly InputQualityGate _quality = quality ?? InputQualityGate.CreateDefault();

    /// <summary>Largest document the quality gate accepts by default, in bytes. A larger one is handed to a person.</summary>
    public const int MaxDocumentBytes = 10 * 1024 * 1024;

    /// <summary>The most a host reads into memory before giving up altogether. Between the two limits a document is handed to a person.</summary>
    public const int HardLimitBytes = 50 * 1024 * 1024;

    /// <summary>The name recorded on the assessment of a document the input checks stopped.</summary>
    public const string InputQualityAssessor = "input-quality";

    public async Task<PipelineResult> ProcessAsync(IntakeDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        // Spans and logs are keyed by the claim id. The document id is a blob or file name and can hold a person's name (PII-01).
        var id = ClaimIds.ForDocument(document.DocumentId).ToString("D");

        using var process = new StageScope(PipelineTelemetry.StageProcess, id);
        try
        {
            var receivedAt = document.ReceivedAt ?? time.GetUtcNow();

            var documentFinding = await RunAsync(PipelineTelemetry.StageInputQuality, id,
                () => _quality.RunAsync(QualityStage.Document, new InputQualityContext(document, null), cancellationToken), _ => null);
            if (documentFinding is not null)
            {
                return await RejectAsync(document, id, receivedAt, documentFinding, [], process, cancellationToken);
            }

            DocumentText text;
            try
            {
                text = await RunAsync(PipelineTelemetry.StageExtractText, id,
                    () => textExtractor.ExtractAsync(document, cancellationToken), _ => null);
            }
            catch (UnreadableDocumentException ex) when (ex.Kind == UnreadableKind.NoText)
            {
                var finding = new QualityFinding(IssueCode.UnreadableDocument, "text_extraction",
                    "No text could be read from the document. It may be a scan or a photograph; a person needs to read it.");
                return await RejectAsync(document, id, receivedAt, finding, [], process, cancellationToken);
            }

            var textFinding = await RunAsync(PipelineTelemetry.StageInputQualityText, id,
                () => _quality.RunAsync(QualityStage.Text, new InputQualityContext(document, text), cancellationToken), _ => null);
            if (textFinding is not null)
            {
                return await RejectAsync(document, id, receivedAt, textFinding, text.Pages, process, cancellationToken);
            }

            var fields = await RunAsync(PipelineTelemetry.StageExtractFields, id,
                () => claimExtractor.ExtractAsync(document.DocumentId, text, cancellationToken), f => ClaimConfidence.Lowest(f));

            var byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

            var policyNumber = byName.TryGetValue(ClaimFields.PolicyNumber, out var pn) && pn.HasValue ? pn.Value!.Trim() : null;
            var policy = policyNumber is null
                ? null
                : await RunAsync(PipelineTelemetry.StageLookupPolicy, id,
                    () => policyLookup.FindAsync(policyNumber, cancellationToken), _ => null);

            var confidence = ClaimConfidence.Lowest(fields);

            // The date the claim was notified: what the document says, otherwise when it was received. Never the wall
            // clock at processing time, so a document gives the same result whenever it is processed.
            var statedNotification = NotificationDateReader.Read(text);
            var notifiedOn = statedNotification ?? UkDate(receivedAt);
            var notifiedSource = statedNotification is null ? NotificationDateSources.Received : NotificationDateSources.Document;

            var issues = await RunAsync(PipelineTelemetry.StageValidate, id,
                () => Task.FromResult(ClaimValidator.Validate(byName, policy, notifiedOn)), _ => confidence);

            ClaimAssessment? assessed = null;
            IReadOnlyList<AuditEntry>? audit = null;
            if (assessment is not null)
            {
                // Advice for the handler: it never changes the claim, and a failure here does not lose the claim.
                var context = new AssessmentContext(ClaimIds.ForDocument(document.DocumentId), byName, notifiedOn, policy);
                var outcome = await RunAsync(PipelineTelemetry.StageAssess, id,
                    () => assessment.RunAsync(context, cancellationToken), _ => confidence);
                assessed = outcome.Assessment;
                audit = outcome.AuditTrail;
            }

            if (detector is not null && await FlagIfAttackAsync(text, cancellationToken) is { } flagged)
            {
                assessed = RaiseForAttack(assessed, flagged);
            }

            // Shared limits (REL-04): a long document or trail is cut down and a person is told, so the API never has to refuse it.
            var proposed = new ProposedClaim(document.DocumentId, receivedAt, fields, [.. issues], text.Pages, assessed, audit, notifiedOn, notifiedSource)
                .WithinLimits();

            await RunAsync(PipelineTelemetry.StageHandoff, id, async () =>
            {
                await sink.HandOffAsync(proposed, cancellationToken);
                return proposed;
            }, _ => confidence);

            var result = new PipelineResult(proposed, confidence);
            process.SetConfidence(confidence);
            LogHandedOff(logger, proposed.ClaimId, result.ErrorCount, result.WarningCount, confidence);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Fail(ex);
            LogFailed(logger, id, ex.GetType().Name);
            throw;
        }
    }

    private static async Task<T> RunAsync<T>(string stage, string claimId, Func<Task<T>> work, Func<T, double?> confidence)
    {
        using var scope = new StageScope(stage, claimId);
        try
        {
            var result = await work();
            if (confidence(result) is { } c)
            {
                scope.SetConfidence(c);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Hands a document that failed an input-quality check to a person: no fields, one blocking issue that says why, and a
    /// senior-review assessment, so it is visible in the review queue instead of failing silently.
    /// </summary>
    private async Task<PipelineResult> RejectAsync(
        IntakeDocument document, string id, DateTimeOffset receivedAt, QualityFinding finding, IReadOnlyList<string> pages,
        StageScope process, CancellationToken cancellationToken)
    {
        var fields = FieldNames.All.Select(FieldNames.NotFound).ToList();
        var assessment = new ClaimAssessment(InputQualityAssessor, [], ReviewRoute.SeniorReview,
            $"Needs a person before any assessment: {finding.Message}", [$"Stopped by the {finding.Check} input check; no fields were extracted."]);
        var issue = new ValidationIssue(finding.Code, IssueSeverity.Error, null, finding.Message);
        var proposed = new ProposedClaim(document.DocumentId, receivedAt, fields, [issue], pages, assessment).WithinLimits();

        await RunAsync(PipelineTelemetry.StageHandoff, id, async () =>
        {
            await sink.HandOffAsync(proposed, cancellationToken);
            return proposed;
        }, _ => 0.0);

        process.SetConfidence(0.0);
        LogRejected(logger, proposed.ClaimId, finding.Code.ToString(), finding.Check);
        return new PipelineResult(proposed, 0.0);
    }

    /// <summary>Asks the prompt-attack detector about the document text. Null when it is clean.</summary>
    private async Task<PromptAttackResult?> FlagIfAttackAsync(DocumentText text, CancellationToken cancellationToken)
    {
        var result = await detector!.DetectAsync(string.Join("\n", text.Pages), cancellationToken);
        return result.Flagged ? result : null;
    }

    /// <summary>A flagged document goes to senior review, with a note that says why (never a quote from the document).</summary>
    private static ClaimAssessment RaiseForAttack(ClaimAssessment? assessed, PromptAttackResult flagged)
    {
        var note = $"The document was flagged as a possible attack on the AI assistant ({flagged.Reason ?? "prompt injection"}). Read it with care.";
        return assessed is null
            ? new ClaimAssessment("prompt-shield", [], ReviewRoute.SeniorReview, "Needs senior review: the document was flagged as a possible prompt attack.", [note])
            : assessed with { Route = ReviewRoute.SeniorReview, RouteReason = "Needs senior review: the document was flagged as a possible prompt attack.", Notes = [.. assessed.Notes, note] };
    }

    /// <summary>The UK calendar date of an instant, since claim dates are UK dates. Falls back to UTC if the zone is unavailable.</summary>
    private static DateOnly UkDate(DateTimeOffset now)
    {
        foreach (var zone in new[] { "Europe/London", "GMT Standard Time" })
        {
            try
            {
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Try the next name.
            }
        }

        return DateOnly.FromDateTime(now.UtcDateTime);
    }

    /// <summary>Removes control characters and caps the length of an id taken from a file or blob name before it is logged.</summary>
    public static string Sanitise(string value)
    {
        var cleaned = new string([.. value.Select(c => char.IsControl(c) ? '_' : c)]);
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Claim {ClaimId} handed off: {ErrorCount} errors, {WarningCount} warnings, lowest confidence {Confidence:F2}.")]
    private static partial void LogHandedOff(ILogger logger, Guid claimId, int errorCount, int warningCount, double confidence);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Claim {ClaimId} handed to a person to read: input check {Check} found {Code}.")]
    private static partial void LogRejected(ILogger logger, Guid claimId, string code, string check);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claim {ClaimId} failed intake: {ErrorType}.")]
    private static partial void LogFailed(ILogger logger, string claimId, string errorType);
}
