using System.Diagnostics;

namespace ClaimsIntake.Domain.Telemetry;

/// <summary>
/// Carries a W3C trace context in the application properties of a message, so a trace can cross a queue (OBS-02). The
/// Service Bus SDK only does this itself behind an experimental switch, so the sender and the receiver do it explicitly.
/// The properties are <c>traceparent</c> and <c>tracestate</c> (W3C Trace Context) and <c>Diagnostic-Id</c>, which is the same
/// value as <c>traceparent</c> and is what the Azure SDKs and Application Insights read on older clients. Only identifiers
/// travel: never claim text.
/// </summary>
public static class TraceContextCarrier
{
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";
    public const string DiagnosticId = "Diagnostic-Id";

    /// <summary>Writes the activity's context into <paramref name="properties"/>. Does nothing when there is no activity, or it has no W3C id.</summary>
    public static void Inject(Activity? activity, IDictionary<string, object> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C || activity.Id is not { Length: > 0 } id)
        {
            return;
        }

        properties[TraceParent] = id;
        properties[DiagnosticId] = id;
        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            properties[TraceState] = activity.TraceStateString;
        }
    }

    /// <summary>Reads the context written by <see cref="Inject"/>. Null when the message carries none or it is malformed.</summary>
    public static ActivityContext? Extract(IReadOnlyDictionary<string, object> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var parent = Text(properties, TraceParent) ?? Text(properties, DiagnosticId);
        if (parent is null)
        {
            return null;
        }

        return ActivityContext.TryParse(parent, Text(properties, TraceState), isRemote: true, out var context) ? context : null;
    }

    private static string? Text(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) && value is string { Length: > 0 } text ? text : null;
}
