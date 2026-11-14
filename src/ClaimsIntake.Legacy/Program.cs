using System.Text.Json.Serialization;
using ClaimsIntake.Domain.Security;
using ClaimsIntake.Legacy;
using ClaimsIntake.Security.AspNetCore;

// A stand-in for Thames Mutual's legacy claims system (fictional). It only imitates the two calls the
// intake needs. A real system would sit behind a firewall and use its own authentication.
var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

// Even the mock needs authentication (SEC-05): it is the system that proves "no write without an approval". Callers present
// app-only Entra ID tokens with the role for what they do. Auth:Mode=Dev is for local development and is refused elsewhere.
builder.AddClaimsIntakeAuthentication(options =>
{
    options.AddAppRolePolicy(Policies.ClaimsWrite, AppRoles.ClaimsWrite);
    options.AddAppRolePolicy(Policies.ClaimsRead, AppRoles.ClaimsRead);
    options.AddAppRolePolicy(Policies.PoliciesRead, AppRoles.PoliciesRead);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPolicyStore, InMemoryPolicyStore>();
builder.Services.AddSingleton<ILegacyClaimStore, InMemoryLegacyClaimStore>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapLegacyEndpoints();

app.Run();

namespace ClaimsIntake.Legacy
{
    /// <summary>Marks this assembly for WebApplicationFactory in tests.</summary>
    public sealed class LegacyMarker;
}
