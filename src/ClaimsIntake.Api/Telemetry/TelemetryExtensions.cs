using System.Diagnostics.Metrics;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Telemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
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

/// <summary>Custom metrics emitted by the API. The names are the contract of the alert rules and the workbook in infra/ (OBS-03).</summary>
public static class ApiMetrics
{
    public const string MeterName = "ClaimsIntake.Api";

    /// <summary>An approved claim that has not reached the legacy system within this time counts as stuck (REL-08, guardrail G1).</summary>
    public static readonly TimeSpan StuckApprovalAfter = TimeSpan.FromMinutes(10);

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

    /// <summary>
    /// Seconds from a document being received to its claim waiting in the review queue (success metric M5: p95 under 90 s).
    /// Tagged size=small (up to 5 pages), medium (up to 20) or large.
    /// </summary>
    public static readonly Histogram<double> TimeToReviewQueue =
        Meter.CreateHistogram<double>("claims.time_to_review_queue", unit: "s", description: "Seconds from a document being received to its claim waiting for review.");

    /// <summary>Accepted claims by the route the assessor gave them, tagged route=StraightToHandler, SeniorReview or none (no assessor ran).</summary>
    public static readonly Counter<long> RouteDistribution =
        Meter.CreateCounter<long>("claims.route", unit: "{claim}", description: "Accepted claims by assessment route.");

    /// <summary>Messages the consumer dead-lettered, tagged reason. An alert fires above zero.</summary>
    public static readonly Counter<long> DeadLettered =
        Meter.CreateCounter<long>("claims.intake.dead_lettered", unit: "{message}", description: "Review-queue messages the consumer dead-lettered.");

    /// <summary>Requests refused with a 429 by the rate limiter, tagged policy.</summary>
    public static readonly Counter<long> RateLimited =
        Meter.CreateCounter<long>("claims.rate_limited", unit: "{request}", description: "Requests refused by the rate limiter.");

    /// <summary>The size band used to tag <see cref="TimeToReviewQueue"/>.</summary>
    public static string SizeBand(int pages) => pages switch
    {
        <= 5 => "small",
        <= 20 => "medium",
        _ => "large",
    };

    /// <summary>Where the stuck-approvals gauge reads its value. Set by <see cref="StuckApprovalsGauge"/>; the host that was built last wins.</summary>
    internal static volatile Func<int>? StuckApprovalsSource;

    /// <summary>
    /// Claims that were approved more than <see cref="StuckApprovalAfter"/> ago and are not yet in the legacy system. Observed on
    /// demand, so it is always current. One instrument for the process; the host reports through <see cref="StuckApprovalsSource"/>.
    /// </summary>
    public static readonly ObservableGauge<int> StuckApprovals =
        Meter.CreateObservableGauge("claims.approved_stuck", () => StuckApprovalsSource?.Invoke() ?? 0, unit: "{claim}",
            description: "Claims approved more than 10 minutes ago and not yet written to the legacy system.");
}

/// <summary>Feeds the gauge of approvals stuck before the legacy write (G1, REL-08) from the claim store.</summary>
public sealed class StuckApprovalsGauge : IDisposable
{
    private readonly IClaimRepository _repository;
    private readonly TimeProvider _time;
    private readonly Func<int> _source;

    public StuckApprovalsGauge(IClaimRepository repository, TimeProvider time)
    {
        _repository = repository;
        _time = time;
        _source = Count;
        ApiMetrics.StuckApprovalsSource = _source;
        GC.KeepAlive(ApiMetrics.StuckApprovals);
    }

    /// <summary>Approved claims decided longer ago than the threshold. Zero if the repository cannot be read: a gauge must not throw.</summary>
    public int Count()
    {
        try
        {
            var now = _time.GetUtcNow();
            return _repository.ListAsync(ClaimStatus.Approved, CancellationToken.None).GetAwaiter().GetResult()
                .Count(c => c.DecidedAt is { } decided && now - decided > ApiMetrics.StuckApprovalAfter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(ApiMetrics.StuckApprovalsSource, _source))
        {
            ApiMetrics.StuckApprovalsSource = null;
        }
    }
}

public static class TelemetryExtensions
{
    /// <summary>The values <c>Telemetry:Exporter</c> accepts.</summary>
    public static readonly IReadOnlyList<string> Exporters = ["console", "otlp", "azuremonitor", "none"];

    /// <summary>Replaces the URL attributes of an outgoing HTTP span with the route template (PII-02).</summary>
    public static void ScrubHttpClientSpan(HttpClientTraceInstrumentationOptions options)
    {
        options.EnrichWithHttpRequestMessage = (activity, request) =>
            UrlScrubber.Scrub(activity, request.RequestUri?.Scheme, request.RequestUri?.Host, request.RequestUri?.Port, request.RequestUri?.AbsolutePath);
        options.EnrichWithHttpResponseMessage = (activity, response) =>
        {
            var uri = response.RequestMessage?.RequestUri;
            UrlScrubber.Scrub(activity, uri?.Scheme, uri?.Host, uri?.Port, uri?.AbsolutePath);
        };
    }

    /// <summary>The route template, not the raw path: a claim id or a policy number must not reach a span (PII-02).</summary>
    public static void ScrubAspNetCoreSpan(AspNetCoreTraceInstrumentationOptions options) =>
        options.EnrichWithHttpResponse = (activity, response) =>
            UrlScrubber.Scrub(activity, response.HttpContext.Request.Scheme, response.HttpContext.Request.Host.Host,
                response.HttpContext.Request.Host.Port, response.HttpContext.Request.Path.Value);

    /// <summary>
    /// Wires OpenTelemetry traces and metrics. The exporter is chosen with <c>Telemetry:Exporter</c>:
    /// "console" (default, for local runs), "otlp" (endpoint from OTEL_EXPORTER_OTLP_ENDPOINT), "azuremonitor" (the Azure Monitor
    /// OpenTelemetry distro, signing in with the managed identity; ingestion needs Entra ID because the Application Insights
    /// resource has local authentication disabled), or "none".
    /// </summary>
    public static WebApplicationBuilder AddClaimsIntakeTelemetry(this WebApplicationBuilder builder)
    {
        var exporter = builder.Configuration["Telemetry:Exporter"]?.Trim().ToLowerInvariant() ?? "console";
        if (!Exporters.Contains(exporter))
        {
            throw new InvalidOperationException(
                $"Telemetry:Exporter must be \"console\", \"otlp\", \"azuremonitor\" or \"none\", not \"{exporter}\".");
        }

        // Both the distro and the plain instrumentation read these options, so the route-template scrubbing applies on every path.
        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(ScrubAspNetCoreSpan);
        builder.Services.Configure<HttpClientTraceInstrumentationOptions>(ScrubHttpClientSpan);

        // Azure SDK spans (Service Bus, Storage) are off in the SDK unless this is set. The pipeline's own spans do not need it.
        AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

        var otel = builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("claims-intake-api"));

        if (exporter == "azuremonitor")
        {
            var connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Telemetry:Exporter is azuremonitor, which needs APPLICATIONINSIGHTS_CONNECTION_STRING.");
            }

            var clientId = builder.Configuration["Intake:ManagedIdentityClientId"] is { Length: > 0 } id ? id : builder.Configuration["AZURE_CLIENT_ID"];
            otel.UseAzureMonitor(options =>
            {
                options.ConnectionString = connectionString;
                // Entra ID, never the instrumentation key: DisableLocalAuth is on for the resource.
                options.Credential = ManagedIdentityCredentialFor(clientId);
                // The default offline store writes telemetry (with span names and ids) to local disk.
                options.DisableOfflineStorage = true;
            });
            otel.WithTracing(tracing => tracing.AddSource(ApiTelemetry.SourceName, "Azure.*"))
                .WithMetrics(metrics => metrics.AddMeter(ApiMetrics.MeterName));
            return builder;
        }

        otel.WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource(ApiTelemetry.SourceName, "Azure.*");
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

    /// <summary>The user-assigned identity when its client id is known, otherwise the default credential chain (a developer sign-in).</summary>
    internal static TokenCredential ManagedIdentityCredentialFor(string? clientId) =>
        string.IsNullOrWhiteSpace(clientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
}
