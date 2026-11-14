using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Hosting;
using ClaimsIntake.Domain.Telemetry;
using ClaimsIntake.Pipeline;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Local mode host (ADR 0005): watches a folder and runs each document through the intake pipeline.
// In Azure the same pipeline runs inside the Function App instead.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddClaimIntakePipeline(builder.Configuration);
builder.Services.AddLocalInboxWatcher(builder.Configuration);

var exporter = builder.Configuration["Telemetry:Exporter"]?.Trim().ToLowerInvariant() ?? "console";
if (exporter is not ("console" or "otlp" or "azuremonitor" or "none"))
{
    throw new InvalidOperationException($"Telemetry:Exporter must be \"console\", \"otlp\", \"azuremonitor\" or \"none\", not \"{exporter}\".");
}

// The local watcher keeps the console and OTLP exporters. "azuremonitor" is there for a watcher run against a real workspace
// (it signs in with DefaultAzureCredential, a developer sign-in); the Function exports with the same exporter in Azure.
var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("claims-intake-watcher"))
    .WithTracing(tracing =>
    {
        tracing.AddSource(PipelineTelemetry.SourceName).AddHttpClientInstrumentation(options =>
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
        switch (exporter)
        {
            case "console":
                tracing.AddConsoleExporter();
                break;
            case "otlp":
                tracing.AddOtlpExporter();
                break;
        }
    });

if (exporter == "azuremonitor")
{
    var connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("Telemetry:Exporter is azuremonitor, which needs APPLICATIONINSIGHTS_CONNECTION_STRING.");
    }

    telemetry.UseAzureMonitorExporter(options =>
    {
        options.ConnectionString = connectionString;
        options.Credential = new DefaultAzureCredential();
        options.DisableOfflineStorage = true;
    });
}

await builder.Build().RunAsync();
