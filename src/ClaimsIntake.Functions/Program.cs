using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using ClaimsIntake.Domain.Telemetry;
using ClaimsIntake.Pipeline;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = FunctionsApplication.CreateBuilder(args);

// Telemetry (OBS-01): the Functions host runs in OpenTelemetry mode (host.json), and the worker exports with the Azure Monitor
// exporter, signing in with the managed identity because the Application Insights resource has local authentication disabled.
// UseFunctionsWorkerDefaults adds the host's own traces and logs; the pipeline's source and the Azure SDK sources are added here,
// which the classic Application Insights path did not do (the stage and tool spans were dropped).
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
builder.Services.Configure<OpenTelemetry.Instrumentation.Http.HttpClientTraceInstrumentationOptions>(options =>
{
    // The route template, not the raw URL: a policy number must not reach a span (PII-02).
    options.EnrichWithHttpRequestMessage = (activity, request) =>
        UrlScrubber.Scrub(activity, request.RequestUri?.Scheme, request.RequestUri?.Host, request.RequestUri?.Port, request.RequestUri?.AbsolutePath);
    options.EnrichWithHttpResponseMessage = (activity, response) =>
    {
        var uri = response.RequestMessage?.RequestUri;
        UrlScrubber.Scrub(activity, uri?.Scheme, uri?.Host, uri?.Port, uri?.AbsolutePath);
    };
});

var telemetry = builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .WithTracing(tracing => tracing.AddSource(PipelineTelemetry.SourceName, "Azure.*").AddHttpClientInstrumentation())
    // The chat client's gen_ai token-usage metrics are emitted on the pipeline's source name; the workbook prices them per claim.
    .WithMetrics(metrics => metrics.AddMeter(PipelineTelemetry.SourceName));

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    var clientId = builder.Configuration["Intake:ManagedIdentityClientId"] is { Length: > 0 } id ? id : builder.Configuration["AZURE_CLIENT_ID"];
    telemetry.UseAzureMonitorExporter(options =>
    {
        options.Credential = string.IsNullOrWhiteSpace(clientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
        options.DisableOfflineStorage = true;
    });
}

// Which extractors and hand-off to use is chosen by the Intake__* app settings (see ServiceCollectionExtensions).
builder.Services.AddClaimIntakePipeline(builder.Configuration);

builder.Build().Run();
