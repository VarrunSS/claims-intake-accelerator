using ClaimsIntake.Pipeline;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Which extractors and hand-off to use is chosen by the Intake__* app settings (see ServiceCollectionExtensions).
builder.Services.AddClaimIntakePipeline(builder.Configuration);

builder.Build().Run();
