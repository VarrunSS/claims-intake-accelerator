using System.Diagnostics;
using ClaimsIntake.Domain.Telemetry;

namespace ClaimsIntake.Domain.Tests;

public class TraceContextCarrierTests
{
    private const string SourceName = "ClaimsIntake.Domain.Tests.Carrier";

    private static readonly ActivitySource Source = new(SourceName);

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            // By name: the source may still be under construction when the listener first asks.
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public void A_context_survives_a_trip_through_message_properties()
    {
        using var listener = Listen();
        using var activity = Source.StartActivity("send")!;
        activity.TraceStateString = "vendor=one";
        var properties = new Dictionary<string, object>();

        TraceContextCarrier.Inject(activity, properties);
        var extracted = TraceContextCarrier.Extract(properties);

        Assert.NotNull(extracted);
        Assert.Equal(activity.TraceId, extracted.Value.TraceId);
        Assert.Equal(activity.SpanId, extracted.Value.SpanId);
        Assert.Equal("vendor=one", extracted.Value.TraceState);
        Assert.True(extracted.Value.IsRemote);
    }

    [Fact]
    public void Inject_writes_traceparent_and_the_diagnostic_id_the_azure_sdks_read()
    {
        using var listener = Listen();
        using var activity = Source.StartActivity("send")!;
        var properties = new Dictionary<string, object>();

        TraceContextCarrier.Inject(activity, properties);

        Assert.Equal(activity.Id, properties[TraceContextCarrier.TraceParent]);
        Assert.Equal(activity.Id, properties[TraceContextCarrier.DiagnosticId]);
        Assert.False(properties.ContainsKey(TraceContextCarrier.TraceState));
    }

    [Fact]
    public void Without_an_activity_nothing_is_written_and_nothing_is_extracted()
    {
        var properties = new Dictionary<string, object>();

        TraceContextCarrier.Inject(null, properties);

        Assert.Empty(properties);
        Assert.Null(TraceContextCarrier.Extract(properties));
    }

    [Fact]
    public void The_diagnostic_id_is_used_when_traceparent_is_missing()
    {
        var properties = new Dictionary<string, object> { [TraceContextCarrier.DiagnosticId] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" };

        var extracted = TraceContextCarrier.Extract(properties);

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", extracted!.Value.TraceId.ToString());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("00-00000000000000000000000000000000-b7ad6b7169203331-01")]
    [InlineData("")]
    public void A_malformed_context_gives_null_not_an_exception(string value)
    {
        var properties = new Dictionary<string, object> { [TraceContextCarrier.TraceParent] = value };

        Assert.Null(TraceContextCarrier.Extract(properties));
    }

    [Fact]
    public void A_property_that_is_not_text_is_ignored()
    {
        var properties = new Dictionary<string, object> { [TraceContextCarrier.TraceParent] = 42 };

        Assert.Null(TraceContextCarrier.Extract(properties));
    }
}
