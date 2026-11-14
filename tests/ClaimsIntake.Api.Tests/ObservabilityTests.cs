using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Azure.Messaging.ServiceBus;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Intake;
using ClaimsIntake.Api.Telemetry;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Domain.Security;
using ClaimsIntake.Domain.Telemetry;
using ClaimsIntake.Pipeline;
using ClaimsIntake.Pipeline.Handoff;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ClaimsIntake.Api.Tests;

/// <summary>
/// OBS-01 to OBS-04: one trace from the pipeline to the API across Service Bus, the metrics the alerts and the workbook read, readiness
/// that reflects the consumer, and the exporter switch. Tests run one at a time (see the assembly attribute), because the listeners
/// below see every claim a test in this process handles.
/// </summary>
public class ObservabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static ProposedClaim Proposed(string documentId, int pages = 1, DateTimeOffset? receivedAt = null, ClaimAssessment? assessment = null) => new(
        documentId,
        receivedAt ?? Now,
        [
            new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.97, new SourceSpan(documentId, 1, 15, 12)),
            new ExtractedField(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.93),
        ],
        [],
        [.. Enumerable.Range(0, pages).Select(i => $"Page {i + 1}\nPolicy number: TM-CP-100001")],
        assessment);

    private sealed class FakeSender : ServiceBusSender
    {
        public List<ServiceBusMessage> Sent { get; } = [];

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ServiceBusReceivedMessage Receive(ServiceBusMessage sent) => ServiceBusModelFactory.ServiceBusReceivedMessage(
        body: sent.Body, messageId: sent.MessageId, properties: new Dictionary<string, object>(sent.ApplicationProperties));

    private static (ClaimMessageProcessor Processor, InMemoryClaimRepository Repository, ConsumerState State) Processor(TimeProvider? time = null)
    {
        var repository = new InMemoryClaimRepository();
        var clock = time ?? new FixedTime(Now);
        var state = new ConsumerState(clock);
        var service = new ClaimIntakeService(repository, new InMemoryAuditLog(), NullLogger<ClaimIntakeService>.Instance, clock);
        return (new ClaimMessageProcessor(service, state), repository, state);
    }

    private static ActivityListener Listen(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is PipelineTelemetry.SourceName or ApiTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (stopped)
                {
                    stopped.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // ----- OBS-02: one trace across Service Bus -----

    [Fact]
    public async Task The_pipeline_side_and_the_api_side_share_one_trace_across_service_bus()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        var sender = new FakeSender();
        var proposed = Proposed("inbox/trace.pdf");
        var (processor, repository, _) = Processor();

        // The Function's invocation (here a root span) hands the claim to the sink, which sends it to the queue.
        using var root = PipelineTelemetry.Source.StartActivity("test.function-invocation");
        Assert.NotNull(root);
        root.TraceStateString = "claims=1";
        await new ServiceBusClaimSink(sender).HandOffAsync(proposed, CancellationToken.None);
        var message = Assert.Single(sender.Sent);
        root.Stop();

        // On the other side nothing is shared with the sender but the message.
        Activity.Current = null;
        await processor.HandleAsync(Receive(message), (_, _, _) => Task.CompletedTask, CancellationToken.None);

        Activity send, consume;
        lock (stopped)
        {
            send = Assert.Single(stopped, a => a.OperationName == "claims.handoff.send");
            consume = Assert.Single(stopped, a => a.OperationName == "claims.intake");
        }

        Assert.Equal(root.TraceId, send.TraceId);
        Assert.Equal(send.TraceId, consume.TraceId);
        Assert.Equal(send.SpanId, consume.ParentSpanId);
        Assert.Equal(ActivityKind.Producer, send.Kind);
        Assert.Equal(ActivityKind.Consumer, consume.Kind);
        Assert.Equal("claims=1", consume.TraceStateString);

        // The claim id is on both sides, and is the only identifier: not the document name.
        var claimId = proposed.ClaimId.ToString("D");
        Assert.Equal(claimId, send.GetTagItem("claimId"));
        Assert.Equal(claimId, consume.GetTagItem("claimId"));
        Assert.NotNull(await repository.GetAsync(proposed.ClaimId, CancellationToken.None));
    }

    [Fact]
    public async Task The_message_carries_the_w3c_context_and_nothing_from_the_claim()
    {
        using var listener = Listen([]);
        var sender = new FakeSender();

        using var root = PipelineTelemetry.Source.StartActivity("test.function-invocation");
        await new ServiceBusClaimSink(sender).HandOffAsync(Proposed("inbox/Smith J - burst pipe.pdf"), CancellationToken.None);

        var properties = sender.Sent.Single().ApplicationProperties;
        Assert.Equal(["Diagnostic-Id", "traceparent"], properties.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var traceparent = Assert.IsType<string>(properties["traceparent"]);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(traceparent, "^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$"), traceparent);
        Assert.Equal(traceparent, properties["Diagnostic-Id"]);
        Assert.DoesNotContain("Smith", string.Join(' ', properties.Values.Select(v => v.ToString())));
    }

    [Fact]
    public async Task A_message_without_a_trace_context_starts_a_trace_of_its_own_and_is_still_accepted()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        var (processor, repository, _) = Processor();
        var proposed = Proposed("inbox/old-sender.pdf");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(proposed.ToJson()), messageId: proposed.ClaimId.ToString("D"));

        Activity.Current = null;
        await processor.HandleAsync(message, (_, _, _) => Task.CompletedTask, CancellationToken.None);

        var consume = Assert.Single(stopped, a => a.OperationName == "claims.intake");
        Assert.Equal(default, consume.ParentSpanId);
        Assert.NotNull(await repository.GetAsync(proposed.ClaimId, CancellationToken.None));
    }

    [Theory]
    [InlineData("not-a-traceparent")]
    [InlineData("00-00000000000000000000000000000000-0000000000000000-01")]
    [InlineData("")]
    public async Task A_malformed_trace_context_is_ignored_not_an_error(string traceparent)
    {
        using var listener = Listen([]);
        var (processor, repository, state) = Processor();
        var proposed = Proposed("inbox/odd.pdf");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(proposed.ToJson()), messageId: proposed.ClaimId.ToString("D"),
            properties: new Dictionary<string, object> { ["traceparent"] = traceparent });

        Activity.Current = null;
        await processor.HandleAsync(message, (_, _, _) => Task.CompletedTask, CancellationToken.None);

        Assert.NotNull(await repository.GetAsync(proposed.ClaimId, CancellationToken.None));
        Assert.Null(state.Snapshot().LastError);
    }

    [Fact]
    public async Task A_message_that_is_not_a_claim_is_dead_lettered_and_counted_and_marks_the_consumer_span_failed()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        using var meters = new Measurements(ApiMetrics.MeterName, "claims.intake.dead_lettered");
        var (processor, _, _) = Processor();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("{not json"), messageId: "m1");
        string? reason = null;

        Activity.Current = null;
        await processor.HandleAsync(message, (r, _, _) => { reason = r; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal("InvalidProposedClaim", reason);
        var counted = Assert.Single(meters.Longs);
        Assert.Equal((1L, "InvalidProposedClaim"), (counted.Value, counted.Tag("reason")));
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(stopped, a => a.OperationName == "claims.intake").Status);
    }

    // ----- OBS-03: metrics -----

    [Fact]
    public async Task Accepting_a_claim_records_the_time_to_the_review_queue_with_a_size_band()
    {
        using var meters = new Measurements(ApiMetrics.MeterName, "claims.time_to_review_queue");
        var received = Now.AddSeconds(-42);
        var (processor, _, _) = Processor();
        var proposed = Proposed("inbox/big.pdf", pages: 25, receivedAt: received);

        await processor.HandleAsync(ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(proposed.ToJson()), messageId: proposed.ClaimId.ToString("D")), (_, _, _) => Task.CompletedTask, CancellationToken.None);

        var recorded = Assert.Single(meters.Doubles);
        Assert.Equal(42.0, recorded.Value, precision: 3);
        Assert.Equal("large", recorded.Tag("size"));
    }

    [Theory]
    [InlineData(1, "small")]
    [InlineData(5, "small")]
    [InlineData(6, "medium")]
    [InlineData(20, "medium")]
    [InlineData(21, "large")]
    public void The_size_bands_are_small_medium_and_large(int pages, string band) => Assert.Equal(band, ApiMetrics.SizeBand(pages));

    [Fact]
    public async Task Accepting_a_claim_counts_it_by_route()
    {
        using var meters = new Measurements(ApiMetrics.MeterName, "claims.route");
        var (processor, _, _) = Processor();
        var senior = new ClaimAssessment("rules", [], ReviewRoute.SeniorReview, "Because.", []);

        foreach (var (name, assessment) in new (string, ClaimAssessment?)[] { ("inbox/r1.pdf", senior), ("inbox/r2.pdf", null) })
        {
            var proposed = Proposed(name, assessment: assessment);
            await processor.HandleAsync(ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: BinaryData.FromString(proposed.ToJson()), messageId: proposed.ClaimId.ToString("D")), (_, _, _) => Task.CompletedTask, CancellationToken.None);
        }

        Assert.Equal(["SeniorReview", "none"], meters.Longs.Select(m => m.Tag("route")).ToArray());
    }

    [Fact]
    public async Task The_stuck_approvals_gauge_counts_claims_approved_more_than_ten_minutes_ago_and_not_submitted()
    {
        var repository = new InMemoryClaimRepository();
        var approver = new Approver("oid-1", "A Patel", [AppRoles.ClaimsHandler]);
        Claim Approved(string id, TimeSpan ago)
        {
            var claim = new Claim(Guid.NewGuid(), id, Now.AddHours(-2),
                [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.97)], [], ["x"]);
            claim.Approve(approver, Now - ago);
            return claim;
        }

        var stuck = Approved("inbox/stuck.pdf", TimeSpan.FromMinutes(11));
        var recent = Approved("inbox/recent.pdf", TimeSpan.FromMinutes(5));
        var submitted = Approved("inbox/done.pdf", TimeSpan.FromMinutes(30));
        submitted.MarkSubmitted("LEG-1", Now);
        foreach (var claim in new[] { stuck, recent, submitted })
        {
            await repository.AddAsync(claim, CancellationToken.None);
        }

        using var gauge = new StuckApprovalsGauge(repository, new FixedTime(Now));
        using var meters = new Measurements(ApiMetrics.MeterName, "claims.approved_stuck");
        meters.Collect();

        Assert.Equal(1, Assert.Single(meters.Ints).Value);
    }

    // ----- OBS-04: readiness reflects the consumer -----

    [Fact]
    public async Task Readiness_is_unhealthy_until_the_consumer_runs_and_again_when_it_stops()
    {
        var state = new ConsumerState(new FixedTime(Now));
        var check = new ConsumerHealthCheck(state, new FixedTime(Now));

        Assert.Equal(HealthStatus.Unhealthy, await Status(check));
        state.MarkRunning();
        Assert.Equal(HealthStatus.Healthy, await Status(check));
        state.MarkStopped("AuthenticationFailedException");
        var stopped = await check.CheckHealthAsync(new(), CancellationToken.None);
        Assert.Equal(HealthStatus.Unhealthy, stopped.Status);
        Assert.Contains("AuthenticationFailedException", stopped.Description);
    }

    [Fact]
    public async Task Readiness_is_degraded_after_a_recent_error_and_recovers_after_a_success_or_the_window()
    {
        var clock = new MutableTime(Now);
        var state = new ConsumerState(clock);
        var check = new ConsumerHealthCheck(state, clock);
        state.MarkRunning();

        state.RecordError();
        Assert.Equal(HealthStatus.Degraded, await Status(check));

        clock.Advance(TimeSpan.FromSeconds(1));
        state.RecordSuccess();
        Assert.Equal(HealthStatus.Healthy, await Status(check));

        clock.Advance(TimeSpan.FromSeconds(1));
        state.RecordError();
        Assert.Equal(HealthStatus.Degraded, await Status(check));
        clock.Advance(ConsumerHealthCheck.ErrorWindow + TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Healthy, await Status(check));
    }

    [Fact]
    public async Task The_ready_probe_answers_503_while_the_service_bus_consumer_is_stopped_and_200_when_it_runs()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Intake:ServiceBus:FullyQualifiedNamespace", "sb-test.servicebus.windows.net");
            builder.ConfigureServices(services =>
            {
                // The real consumer would reach for Service Bus. Its state is what the probe reads.
                var consumer = services.Single(d => d.ImplementationType == typeof(ServiceBusClaimConsumer));
                services.Remove(consumer);
            });
        });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        factory.Services.GetRequiredService<ConsumerState>().MarkRunning();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        factory.Services.GetRequiredService<ConsumerState>().MarkStopped(null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Without_a_service_bus_namespace_there_is_no_consumer_check_and_the_probe_is_healthy()
    {
        using var factory = new ApiFactory();

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health/ready")).StatusCode);
    }

    // ----- OBS-01: the exporter switch -----

    [Fact]
    public void An_unknown_exporter_stops_the_api_from_starting()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("Telemetry:Exporter", "datadog"));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("azuremonitor", error.Message);
    }

    [Fact]
    public void The_azure_monitor_exporter_needs_a_connection_string()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("Telemetry:Exporter", "azuremonitor"));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("APPLICATIONINSIGHTS_CONNECTION_STRING", error.Message);
    }

    [Fact]
    public void The_azure_monitor_exporter_signs_in_with_the_managed_identity_and_not_a_key()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Telemetry:Exporter", "azuremonitor");
            builder.UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost:1/");
            builder.UseSetting("Intake:ManagedIdentityClientId", "44444444-4444-4444-4444-444444444444");
            builder.UseSetting("AzureMonitor:EnableLiveMetrics", "false");
        });

        _ = factory.CreateClient();

        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Azure.Monitor.OpenTelemetry.AspNetCore.AzureMonitorOptions>>().Value;
        Assert.IsType<Azure.Identity.ManagedIdentityCredential>(options.Credential);
        Assert.True(options.DisableOfflineStorage);
        Assert.Contains("localhost", options.ConnectionString);
        Assert.NotNull(factory.Services.GetService<OpenTelemetry.Trace.TracerProvider>());
        Assert.NotNull(factory.Services.GetService<OpenTelemetry.Metrics.MeterProvider>());
    }

    [Fact]
    public void The_local_exporters_register_the_api_source_and_meter()
    {
        using var factory = new ApiFactory();
        _ = factory.CreateClient();

        Assert.NotNull(factory.Services.GetService<OpenTelemetry.Trace.TracerProvider>());
        Assert.True(ApiTelemetry.Source.HasListeners());
    }

    // ----- helpers -----

    private static async Task<HealthStatus> Status(ConsumerHealthCheck check) =>
        (await check.CheckHealthAsync(new(), CancellationToken.None)).Status;

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Collects what the named instruments of one meter record while the object lives.</summary>
    private sealed class Measurements : IDisposable
    {
        private readonly MeterListener _listener = new();

        public Measurements(string meter, params string[] instruments)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meter && instruments.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => Longs.Add(new Recorded<long>(value, tags.ToArray())));
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) => Doubles.Add(new Recorded<double>(value, tags.ToArray())));
            _listener.SetMeasurementEventCallback<int>((_, value, tags, _) => Ints.Add(new Recorded<int>(value, tags.ToArray())));
            _listener.Start();
        }

        public List<Recorded<long>> Longs { get; } = [];

        public List<Recorded<double>> Doubles { get; } = [];

        public List<Recorded<int>> Ints { get; } = [];

        public void Collect() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Recorded<T>(T Value, KeyValuePair<string, object?>[] Tags)
    {
        public object? Tag(string name) => Tags.FirstOrDefault(t => t.Key == name).Value;
    }
}
