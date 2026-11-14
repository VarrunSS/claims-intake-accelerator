using System.Diagnostics;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Text;

namespace ClaimsIntake.Pipeline.Tests;

public class ClaimIntakePipelineTests
{
    private const string Clean = "01-water-damage-clean.pdf";

    /// <summary>Collects the pipeline's spans for one document. Filtering by document id keeps parallel tests apart.</summary>
    private sealed class SpanCollector : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly string _documentId;
        private readonly List<Activity> _spans = [];

        public SpanCollector(string documentId)
        {
            _documentId = documentId;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_spans)
                    {
                        _spans.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Spans
        {
            get
            {
                lock (_spans)
                {
                    return [.. _spans.Where(a => a.GetTagItem(PipelineTelemetry.DocumentId) as string == _documentId)];
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private static IntakeDocument UniqueSample(string sample, out string documentId)
    {
        var original = Docs.Sample(sample);
        documentId = $"inbox/{Guid.NewGuid():N}-{sample}";
        return original with { DocumentId = documentId };
    }

    [Fact]
    public async Task A_clean_document_is_extracted_validated_and_handed_off_once()
    {
        var sink = new RecordingSink();
        var policies = FakePolicyLookup.Seeded();

        var result = await Docs.Pipeline(policies, sink).ProcessAsync(Docs.Sample(Clean), CancellationToken.None);

        var proposed = Assert.Single(sink.Claims);
        Assert.Same(proposed, result.Claim);
        Assert.Equal($"inbox/{Clean}", proposed.DocumentId);
        Assert.Equal(TestTime.Now, proposed.ReceivedAt);
        Assert.Equal(["TM-CP-100001"], policies.Lookups);
        Assert.Empty(proposed.Issues);
        Assert.Equal(5, proposed.Fields.Count);
        Assert.Contains("Policy number: TM-CP-100001", Assert.Single(proposed.SourcePages));
        Assert.Equal(ClaimIds.ForDocument($"inbox/{Clean}"), proposed.ClaimId);
    }

    [Fact]
    public async Task An_unknown_policy_is_reported_as_an_error_but_still_goes_to_a_handler()
    {
        var sink = new RecordingSink();
        var pipeline = Docs.Pipeline(policies: new FakePolicyLookup(), sink: sink);

        var result = await pipeline.ProcessAsync(Docs.Sample(Clean), CancellationToken.None);

        Assert.Contains(result.Claim.Issues, i => i.Code == IssueCode.UnknownPolicy && i.Severity == IssueSeverity.Error);
        Assert.Single(sink.Claims);
    }

    [Fact]
    public async Task Each_stage_gets_a_span_with_stage_duration_and_document_id()
    {
        var document = UniqueSample(Clean, out var id);
        using var spans = new SpanCollector(id);

        await Docs.Pipeline().ProcessAsync(document, CancellationToken.None);

        var byStage = spans.Spans.ToDictionary(s => (string)s.GetTagItem(PipelineTelemetry.Stage)!);
        Assert.Equal(
            ["extract_fields", "extract_text", "handoff", "lookup_policy", "process", "validate"],
            byStage.Keys.Order());

        foreach (var span in byStage.Values)
        {
            Assert.Equal(id, span.GetTagItem(PipelineTelemetry.DocumentId));
            Assert.IsType<double>(span.GetTagItem(PipelineTelemetry.DurationMs));
            Assert.True((double)span.GetTagItem(PipelineTelemetry.DurationMs)! >= 0);
            Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        }

        // The stages that know the claim's fields report its lowest confidence; the parent span does too.
        foreach (var stage in new[] { "extract_fields", "validate", "handoff", "process" })
        {
            Assert.Equal(0.88, byStage[stage].GetTagItem(PipelineTelemetry.Confidence));
        }

        Assert.Equal(byStage["process"].SpanId, byStage["extract_text"].ParentSpanId);
    }

    [Fact]
    public async Task Spans_and_logs_carry_ids_and_numbers_but_never_document_text()
    {
        var logger = new ListLogger<ClaimIntakePipeline>();
        var document = UniqueSample(Clean, out var id);
        using var spans = new SpanCollector(id);

        await Docs.Pipeline(logger: logger).ProcessAsync(document, CancellationToken.None);

        var everything = string.Join('\n', logger.Lines.Concat(
            spans.Spans.SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName))));
        Assert.NotEmpty(logger.Lines);
        foreach (var secret in new[] { "Brindle", "Marsh", "water pipe", "12,500", "Sam Brindle", "TM-CP-100001" })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }

    [Fact]
    public async Task A_failure_marks_the_span_with_the_exception_type_and_not_its_message()
    {
        var document = UniqueSample(Clean, out var id);
        using var spans = new SpanCollector(id);
        var pipeline = Docs.Pipeline(policies: new DownPolicyLookup());

        await Assert.ThrowsAsync<LegacySystemUnavailableException>(() => pipeline.ProcessAsync(document, CancellationToken.None));

        var failed = spans.Spans.Single(s => (string)s.GetTagItem(PipelineTelemetry.Stage)! == "lookup_policy");
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Equal(nameof(LegacySystemUnavailableException), failed.StatusDescription);
        Assert.Equal(ActivityStatusCode.Error, spans.Spans.Single(s => (string)s.GetTagItem(PipelineTelemetry.Stage)! == "process").Status);
        Assert.DoesNotContain(spans.Spans, s => (string)s.GetTagItem(PipelineTelemetry.Stage)! == "handoff");
    }

    [Fact]
    public async Task When_the_legacy_system_is_down_nothing_is_handed_off()
    {
        var sink = new RecordingSink();

        await Assert.ThrowsAsync<LegacySystemUnavailableException>(
            () => Docs.Pipeline(policies: new DownPolicyLookup(), sink: sink).ProcessAsync(Docs.Sample(Clean), CancellationToken.None));

        Assert.Empty(sink.Claims);
    }

    [Fact]
    public async Task When_the_hand_off_fails_the_error_reaches_the_caller_so_the_document_can_be_retried()
    {
        var sink = new RecordingSink { FailWith = new HandoffFailedException("queue down") };

        await Assert.ThrowsAsync<HandoffFailedException>(
            () => Docs.Pipeline(sink: sink).ProcessAsync(Docs.Sample(Clean), CancellationToken.None));
    }

    [Fact]
    public async Task A_second_run_of_the_same_document_hands_off_the_same_claim_id()
    {
        var sink = new RecordingSink();
        var pipeline = Docs.Pipeline(sink: sink);

        await pipeline.ProcessAsync(Docs.Sample(Clean), CancellationToken.None);
        await pipeline.ProcessAsync(Docs.Sample(Clean), CancellationToken.None);

        Assert.Equal(2, sink.Claims.Count);
        Assert.Equal(sink.Claims[0].ClaimId, sink.Claims[1].ClaimId);
    }

    [Fact]
    public async Task Empty_oversized_and_unreadable_documents_are_refused_before_any_hand_off()
    {
        var sink = new RecordingSink();
        var pipeline = Docs.Pipeline(sink: sink);

        await Assert.ThrowsAsync<UnreadableDocumentException>(() =>
            pipeline.ProcessAsync(new IntakeDocument("inbox/e.txt", "e.txt", BinaryData.FromBytes([])), CancellationToken.None));
        await Assert.ThrowsAsync<UnreadableDocumentException>(() =>
            pipeline.ProcessAsync(
                new IntakeDocument("inbox/big.txt", "big.txt", BinaryData.FromBytes(new byte[ClaimIntakePipeline.MaxDocumentBytes + 1])),
                CancellationToken.None));
        await Assert.ThrowsAsync<UnreadableDocumentException>(() =>
            pipeline.ProcessAsync(Docs.Text("x.docx", "hello"), CancellationToken.None));

        Assert.Empty(sink.Claims);
    }

    [Fact]
    public async Task A_model_failure_in_the_extractor_stops_the_run()
    {
        var sink = new RecordingSink();
        var extractor = new FoundryLlmClaimExtractor(new FakeChatClient("not json"));

        await Assert.ThrowsAsync<ClaimExtractionException>(
            () => Docs.Pipeline(sink: sink, extractor: extractor).ProcessAsync(Docs.Sample(Clean), CancellationToken.None));

        Assert.Empty(sink.Claims);
    }

    [Fact]
    public async Task The_llm_extractor_plugs_into_the_same_pipeline()
    {
        var reply = """
            {
              "policy_number": { "value": "TM-CP-100001", "quote": "Policy number: TM-CP-100001", "confidence": 0.99 },
              "claimant_name": { "value": "Brindle & Marsh Joinery Ltd", "quote": "Insured: Brindle & Marsh Joinery Ltd", "confidence": 0.95 },
              "loss_date": { "value": "2026-11-03", "quote": "Date of loss: 3 November 2026", "confidence": 0.9 },
              "loss_description": { "value": null, "quote": null, "confidence": 0 },
              "estimated_amount_gbp": { "value": "£12,500", "quote": "Estimated amount: £12,500", "confidence": 0.92 }
            }
            """;
        var pipeline = Docs.Pipeline(extractor: new FoundryLlmClaimExtractor(new FakeChatClient(reply)));

        var result = await pipeline.ProcessAsync(Docs.Sample(Clean), CancellationToken.None);

        Assert.Equal(0.9, result.Confidence);
        Assert.Empty(result.Claim.Issues); // The model's date is "2026-11-03", which the validator reads.
    }

    [Fact]
    public void Control_characters_are_removed_from_ids_before_they_are_logged()
    {
        Assert.Equal("inbox/a_b_c", ClaimIntakePipeline.Sanitise("inbox/a\rb\nc"));
        Assert.Equal(200, ClaimIntakePipeline.Sanitise(new string('a', 500)).Length);
    }

    [Fact]
    public async Task Cancellation_is_passed_through_untouched()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var pipeline = Docs.Pipeline(extractor: new CancellingExtractor());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ProcessAsync(Docs.Sample(Clean), cts.Token));
    }

    private sealed class CancellingExtractor : IClaimExtractor
    {
        public Task<IReadOnlyList<ExtractedField>> ExtractAsync(string documentId, DocumentText text, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }
}
