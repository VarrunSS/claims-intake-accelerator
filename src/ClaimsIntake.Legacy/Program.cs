using System.Text.Json.Serialization;
using ClaimsIntake.Legacy;

// A stand-in for Thames Mutual's legacy claims system (fictional). It only imitates the two calls the
// intake needs. A real system would sit behind a firewall and use its own authentication.
var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPolicyStore, InMemoryPolicyStore>();
builder.Services.AddSingleton<ILegacyClaimStore, InMemoryLegacyClaimStore>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health/live");
app.MapLegacyEndpoints();

app.Run();

namespace ClaimsIntake.Legacy
{
    /// <summary>Marks this assembly for WebApplicationFactory in tests.</summary>
    public sealed class LegacyMarker;
}
