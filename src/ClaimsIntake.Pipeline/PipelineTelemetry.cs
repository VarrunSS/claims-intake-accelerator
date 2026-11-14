using System.Diagnostics;

namespace ClaimsIntake.Pipeline;

/// <summary>
/// One <see cref="Activity"/> span per pipeline stage. Attributes are ids, names and numbers only:
/// never document text, field values or personal data.
/// </summary>
public static class PipelineTelemetry
{
    public const string SourceName = "ClaimsIntake.Pipeline";

    public static readonly ActivitySource Source = new(SourceName);

    // Attribute names.
    public const string Stage = "stage";
    public const string DurationMs = "duration_ms";
    public const string DocumentId = "documentId";
    public const string Confidence = "confidence";

    // Stage names.
    public const string StageProcess = "process";
    public const string StageInputQuality = "input_quality";
    public const string StageInputQualityText = "input_quality_text";
    public const string StageExtractText = "extract_text";
    public const string StageExtractFields = "extract_fields";
    public const string StageLookupPolicy = "lookup_policy";
    public const string StageValidate = "validate";
    public const string StageAssess = "assess";
    public const string StageHandoff = "handoff";
}

/// <summary>Times a stage and records its attributes on the span when it ends.</summary>
internal sealed class StageScope : IDisposable
{
    private readonly Activity? _activity;
    private readonly long _start = Stopwatch.GetTimestamp();

    public StageScope(string stage, string documentId)
    {
        _activity = PipelineTelemetry.Source.StartActivity($"intake.{stage}");
        _activity?.SetTag(PipelineTelemetry.Stage, stage);
        _activity?.SetTag(PipelineTelemetry.DocumentId, documentId);
    }

    public void SetConfidence(double confidence) => _activity?.SetTag(PipelineTelemetry.Confidence, Math.Round(confidence, 2));

    public void Fail(Exception ex)
    {
        // The type only: messages from parsers and services can quote document content.
        _activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
        _activity?.SetTag("error.type", ex.GetType().Name);
    }

    public void Dispose()
    {
        _activity?.SetTag(PipelineTelemetry.DurationMs, Math.Round(Stopwatch.GetElapsedTime(_start).TotalMilliseconds, 1));
        _activity?.Dispose();
    }
}
