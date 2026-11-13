using System.Text.Json.Serialization;
using ClaimsIntake.Api;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);

// In-memory store for Milestone 1. It sits behind IClaimRepository so a durable store can replace it
// without touching the endpoints.
builder.Services.AddSingleton<IClaimRepository, InMemoryClaimRepository>();

builder.Services.AddHealthChecks().AddCheck<ClaimRepositoryHealthCheck>("claim-repository", tags: ["ready"]);
builder.AddClaimsIntakeTelemetry();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Seed:DemoData"))
{
    var repository = app.Services.GetRequiredService<IClaimRepository>();
    foreach (var claim in DemoData.Create(app.Services.GetRequiredService<TimeProvider>()))
    {
        await repository.AddAsync(claim, CancellationToken.None);
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
app.MapClaimEndpoints();

app.Run();

namespace ClaimsIntake.Api
{
    /// <summary>Marks this assembly for WebApplicationFactory in tests.</summary>
    public sealed class ApiMarker;
}
