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
if (exporter is not ("console" or "otlp" or "none"))
{
    throw new InvalidOperationException($"Telemetry:Exporter must be \"console\", \"otlp\" or \"none\", not \"{exporter}\".");
}

builder.Services.AddOpenTelemetry()
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

await builder.Build().RunAsync();
