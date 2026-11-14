using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
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
/// The intake logic, free of any host: extract text, extract fields, look up the policy, validate, hand off.
/// The Azure Function, the local inbox watcher and the tests all call this class. It only proposes a claim;
/// the write to the legacy system happens after a handler approves (ADR 0002).
/// </summary>
public sealed partial class ClaimIntakePipeline(
    IDocumentTextExtractor textExtractor,
    IClaimExtractor claimExtractor,
    IPolicyLookup policyLookup,
    IClaimSink sink,
    TimeProvider time,
    ILogger<ClaimIntakePipeline> logger)
{
    /// <summary>Largest document accepted, in bytes.</summary>
    public const int MaxDocumentBytes = 10 * 1024 * 1024;

    public async Task<PipelineResult> ProcessAsync(IntakeDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var id = Sanitise(document.DocumentId);

        using var process = new StageScope(PipelineTelemetry.StageProcess, id);
        try
        {
            var size = document.Content.ToMemory().Length;
            if (size == 0 || size > MaxDocumentBytes)
            {
                throw new UnreadableDocumentException(
                    size == 0 ? "The document is empty." : $"The document is larger than {MaxDocumentBytes / (1024 * 1024)} MB.");
            }

            var text = await RunAsync(PipelineTelemetry.StageExtractText, id,
                () => textExtractor.ExtractAsync(document, cancellationToken), _ => null);

            var fields = await RunAsync(PipelineTelemetry.StageExtractFields, id,
                () => claimExtractor.ExtractAsync(document.DocumentId, text, cancellationToken), f => ClaimConfidence.Lowest(f));

            var byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

            var policyNumber = byName.TryGetValue(ClaimFields.PolicyNumber, out var pn) && pn.HasValue ? pn.Value!.Trim() : null;
            var policy = policyNumber is null
                ? null
                : await RunAsync(PipelineTelemetry.StageLookupPolicy, id,
                    () => policyLookup.FindAsync(policyNumber, cancellationToken), _ => null);

            var confidence = ClaimConfidence.Lowest(fields);
            var issues = await RunAsync(PipelineTelemetry.StageValidate, id,
                () => Task.FromResult(ClaimValidator.Validate(byName, policy, Today())), _ => confidence);

            var proposed = new ProposedClaim(document.DocumentId, time.GetUtcNow(), fields, [.. issues], text.Pages);

            await RunAsync(PipelineTelemetry.StageHandoff, id, async () =>
            {
                await sink.HandOffAsync(proposed, cancellationToken);
                return proposed;
            }, _ => confidence);

            var result = new PipelineResult(proposed, confidence);
            process.SetConfidence(confidence);
            LogHandedOff(logger, id, proposed.ClaimId, result.ErrorCount, result.WarningCount, confidence);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Fail(ex);
            LogFailed(logger, id, ex.GetType().Name);
            throw;
        }
    }

    private static async Task<T> RunAsync<T>(string stage, string documentId, Func<Task<T>> work, Func<T, double?> confidence)
    {
        using var scope = new StageScope(stage, documentId);
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

    /// <summary>Today's date in the UK, since claim dates are UK dates. Falls back to UTC if the zone is unavailable.</summary>
    private DateOnly Today()
    {
        var now = time.GetUtcNow();
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
        Message = "Document {DocumentId} became claim {ClaimId}: {ErrorCount} errors, {WarningCount} warnings, lowest confidence {Confidence:F2}.")]
    private static partial void LogHandedOff(ILogger logger, string documentId, Guid claimId, int errorCount, int warningCount, double confidence);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} failed intake: {ErrorType}.")]
    private static partial void LogFailed(ILogger logger, string documentId, string errorType);
}
