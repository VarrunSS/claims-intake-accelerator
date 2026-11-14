using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ClaimsIntake.Api.Telemetry;

/// <summary>The API's own trace source.</summary>
public static class ApiTelemetry
{
    public const string SourceName = "ClaimsIntake.Api";

    public static readonly System.Diagnostics.ActivitySource Source = new(SourceName);
}

/// <summary>Custom metrics emitted by the API.</summary>
public static class ApiMetrics
{
    public const string MeterName = "ClaimsIntake.Api";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Handler decisions, tagged decision=approved or decision=rejected.</summary>
    public static readonly Counter<long> Decisions =
        Meter.CreateCounter<long>("claims.decisions", unit: "{decision}", description: "Claims approved or rejected by a handler.");

    /// <summary>Writes to the legacy system, tagged outcome=created, replayed, refused or unavailable.</summary>
    public static readonly Counter<long> LegacySubmissions =
        Meter.CreateCounter<long>("claims.legacy_submissions", unit: "{submission}", description: "Attempts to write an approved claim to the legacy system.");

    /// <summary>Proposed claims received from intake, tagged outcome=accepted or duplicate.</summary>
    public static readonly Counter<long> IntakeClaims =
        Meter.CreateCounter<long>("claims.intake", unit: "{claim}", description: "Proposed claims received from the intake pipeline.");
}

public static class TelemetryExtensions
{
    /// <summary>
    /// Wires OpenTelemetry traces and metrics. The exporter is chosen with Telemetry:Exporter:
    /// "console" (default), "otlp" (endpoint from OTEL_EXPORTER_OTLP_ENDPOINT) or "none".
    /// Azure Monitor export is added in Milestone 6.
    /// </summary>
    public static WebApplicationBuilder AddClaimsIntakeTelemetry(this WebApplicationBuilder builder)
    {
        var exporter = builder.Configuration["Telemetry:Exporter"]?.Trim().ToLowerInvariant() ?? "console";
        if (exporter is not ("console" or "otlp" or "none"))
        {
            throw new InvalidOperationException(
                $"Telemetry:Exporter must be \"console\", \"otlp\" or \"none\", not \"{exporter}\".");
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("claims-intake-api"))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource(ApiTelemetry.SourceName);
                switch (exporter)
                {
                    case "console":
                        tracing.AddConsoleExporter();
                        break;
                    case "otlp":
                        tracing.AddOtlpExporter();
                        break;
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation()
                    .AddMeter(ApiMetrics.MeterName);
                switch (exporter)
                {
                    case "console":
                        metrics.AddConsoleExporter();
                        break;
                    case "otlp":
                        metrics.AddOtlpExporter();
                        break;
                }
            });

        return builder;
    }
}
