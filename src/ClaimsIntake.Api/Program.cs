using System.Text.Json.Serialization;
using ClaimsIntake.Api;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Intake;
using ClaimsIntake.Api.Legacy;
using ClaimsIntake.Api.Telemetry;

const string LocalDevCors = "local-dev";

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);

// In-memory store. It sits behind IClaimRepository so a durable store can replace it without touching the endpoints.
builder.Services.AddSingleton<IClaimRepository, InMemoryClaimRepository>();

// Approval writes to the legacy system with the claim id as idempotency key, so retries are safe and the
// standard resilience handler (timeouts, retries, circuit breaker) may repeat the POST.
var legacyBase = builder.Configuration["Legacy:BaseUrl"] is { Length: > 0 } configured ? configured : "http://localhost:5200";
builder.Services.AddHttpClient<ILegacyClaimWriter, LegacyClaimWriter>(client =>
        client.BaseAddress = new Uri(legacyBase.EndsWith('/') ? legacyBase : legacyBase + "/"))
    .AddStandardResilienceHandler();
builder.Services.AddSingleton<ClaimApprovalService>();

// Hand-off from intake: HTTP in local mode (ADR 0005), Service Bus in Azure.
builder.Services.AddSingleton<ClaimIntakeService>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Intake:ServiceBus:FullyQualifiedNamespace"]))
{
    builder.Services.AddHostedService<ServiceBusClaimConsumer>();
}

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = IntakeEndpoints.MaxBodyBytes);

// The review UI is served by the Angular dev server and reaches the API through its proxy, so CORS is not
// needed there. It is allowed for the dev server's origin in Development only, and never otherwise.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options => options.AddPolicy(LocalDevCors, policy => policy
        .WithOrigins("http://localhost:4200", "http://127.0.0.1:4200")
        .WithMethods("GET", "POST")
        .WithHeaders("Content-Type")));
}

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
if (app.Environment.IsDevelopment())
{
    app.UseCors(LocalDevCors);
}

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
app.MapClaimEndpoints();
app.MapIntakeEndpoints(app.Configuration);

app.Run();

namespace ClaimsIntake.Api
{
    /// <summary>Marks this assembly for WebApplicationFactory in tests.</summary>
    public sealed class ApiMarker;
}
