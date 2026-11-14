using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ClaimsIntake.Api;
using ClaimsIntake.Api.Audit;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Http;
using ClaimsIntake.Api.Intake;
using ClaimsIntake.Api.Legacy;
using ClaimsIntake.Api.Telemetry;
using ClaimsIntake.Domain.Security;
using ClaimsIntake.Security;
using ClaimsIntake.Security.AspNetCore;
using Azure.Core;
using Azure.Identity;

const string LocalDevCors = "local-dev";

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// RFC 7807 problem details for every error the API produces: validation, 401 and 403 from authentication, 404, 413, 429 and 500.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    // A correlation id for support, never a stack trace or a message from an exception.
    context.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
});
builder.Services.AddClaimsIntakeRateLimiting(builder.Configuration);
builder.Services.AddClaimsIntakeOpenApi();
builder.Services.AddSingleton(TimeProvider.System);

// Authentication (ADR 0009): Microsoft Entra ID JWT bearer, with a fallback policy that requires a signed-in caller on every
// endpoint. Only the health probes opt out. Auth:Mode=Dev swaps in a local scheme and is refused outside Development.
builder.AddClaimsIntakeAuthentication(options =>
{
    options.AddHandlerPolicy();
    options.AddAppRolePolicy(Policies.ReadHistory, AppRoles.IntakeReadHistory);
    options.AddAppRolePolicy(Policies.IntakeSubmit, AppRoles.IntakeSubmit);
});
builder.Services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = builder.Configuration["Intake:ManagedIdentityClientId"] is { Length: > 0 } id ? id : null,
}));

// In-memory store. It sits behind IClaimRepository so a durable store can replace it without touching the endpoints.
builder.Services.AddSingleton<IClaimRepository, InMemoryClaimRepository>();

// Audit trail of tool calls: in memory locally, Azure Table Storage when Audit:Table:Endpoint is set.
builder.Services.AddAuditLog(builder.Configuration);

// Approval writes to the legacy system with the claim id as idempotency key, so retries are safe and the
// standard resilience handler (timeouts, retries, circuit breaker) may repeat the POST.
var legacyBase = builder.Configuration["Legacy:BaseUrl"] is { Length: > 0 } configured ? configured : "http://localhost:5200";
var legacyClient = builder.Services.AddHttpClient<ILegacyClaimWriter, LegacyClaimWriter>(client =>
    client.BaseAddress = new Uri(legacyBase.EndsWith('/') ? legacyBase : legacyBase + "/"));
legacyClient.AddStandardResilienceHandler();
// App-only token for the legacy system's Claims.Write role: Legacy:Scope on Azure, Legacy:DevPersona locally (Development only).
// Added after the resilience handler, so each retry carries a token.
legacyClient.AddServiceAuthentication(builder.Configuration["Legacy:Scope"], builder.Configuration["Legacy:DevPersona"]);
builder.Services.AddSingleton<ClaimDecisionService>();

// Hand-off from intake: HTTP in local mode (ADR 0005), Service Bus in Azure.
builder.Services.AddSingleton<ClaimIntakeService>();
builder.Services.AddSingleton<ConsumerState>();
builder.Services.AddSingleton<ClaimMessageProcessor>();
var consumerEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["Intake:ServiceBus:FullyQualifiedNamespace"]);
if (consumerEnabled)
{
    builder.Services.AddHostedService<ServiceBusClaimConsumer>();
}

// The default request body limit is small (64 KB, INP-03). Only the intake route raises it, in UseBodyLimits.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = BodyLimits.DefaultBytes);

// The review UI is served by the Angular dev server and reaches the API through its proxy, so CORS is not
// needed there. It is allowed for the dev server's origin in Development only, and never otherwise.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options => options.AddPolicy(LocalDevCors, policy => policy
        .WithOrigins("http://localhost:4200", "http://127.0.0.1:4200")
        .WithMethods("GET", "POST")
        .WithHeaders("Content-Type", "Authorization")));
}

var health = builder.Services.AddHealthChecks().AddCheck<ClaimRepositoryHealthCheck>("claim-repository", tags: ["ready"]);
if (consumerEnabled)
{
    // Not ready when nothing is reading the review queue (OBS-04).
    health.AddCheck<ConsumerHealthCheck>("service-bus-consumer", tags: ["ready"]);
}

builder.Services.AddSingleton<StuckApprovalsGauge>();
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

// Created once so the gauge of approvals stuck before the legacy write reports from the start (OBS-03).
app.Services.GetRequiredService<StuckApprovalsGauge>();

app.UseBodyLimits();
app.UseAuthentication();
app.UseAuthorization();
// After authentication, so the limiter can tell callers apart by their object id.
app.UseRateLimiter();

// The probes are anonymous: they say only whether the process is up, and hold no claim data.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapClaimEndpoints();
app.MapIntakeEndpoints(app.Configuration, app.Environment);
if (app.Environment.IsDevelopment())
{
    // The API description is served in Development only. The committed copy is docs/openapi/review-api.v1.json.
    app.MapOpenApi().AllowAnonymous();
}

app.Run();

namespace ClaimsIntake.Api
{
    /// <summary>Marks this assembly for WebApplicationFactory in tests.</summary>
    public sealed class ApiMarker;
}
