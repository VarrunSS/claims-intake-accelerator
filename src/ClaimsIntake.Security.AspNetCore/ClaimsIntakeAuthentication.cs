using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ClaimsIntake.Domain.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace ClaimsIntake.Security.AspNetCore;

/// <summary>Names of the authorisation policies. Endpoints refer to these, never to role strings.</summary>
public static class Policies
{
    /// <summary>A person with the ClaimsHandler or SeniorHandler role, signed in with a user token (not an app-only token).</summary>
    public const string Handler = "Handler";

    /// <summary>App-only token with the Intake.ReadHistory role: the pipeline's duplicate check.</summary>
    public const string ReadHistory = "ReadHistory";

    /// <summary>App-only token with the Intake.Submit role: the local HTTP hand-off.</summary>
    public const string IntakeSubmit = "IntakeSubmit";

    /// <summary>Legacy system: app-only token with Claims.Write.</summary>
    public const string ClaimsWrite = "ClaimsWrite";

    /// <summary>Legacy system: app-only token with Claims.Read.</summary>
    public const string ClaimsRead = "ClaimsRead";

    /// <summary>Legacy system: app-only token with Policies.Read.</summary>
    public const string PoliciesRead = "PoliciesRead";
}

public static class ClaimsPrincipalExtensions
{
    private const string ObjectIdLong = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>True for an app-only (client credentials) token: the <c>idtyp</c> claim is <c>app</c>.</summary>
    public static bool IsAppOnly(this ClaimsPrincipal user) =>
        string.Equals(user.FindFirstValue("idtyp"), "app", StringComparison.Ordinal);

    public static bool HasAppRole(this ClaimsPrincipal user, string role) =>
        user.FindAll("roles").Concat(user.FindAll(ClaimTypes.Role)).Any(c => string.Equals(c.Value, role, StringComparison.Ordinal));

    /// <summary>The caller as an <see cref="Approver"/>: object id and display name from the token, roles from its app roles. Null when the token has no object id.</summary>
    public static Approver? ToApprover(this ClaimsPrincipal user)
    {
        var oid = user.FindFirstValue("oid") ?? user.FindFirstValue(ObjectIdLong);
        if (string.IsNullOrWhiteSpace(oid))
        {
            return null;
        }

        var roles = user.FindAll("roles").Concat(user.FindAll(ClaimTypes.Role)).Select(c => c.Value);
        return new Approver(oid, user.FindFirstValue("name"), roles);
    }
}

/// <summary>
/// Authentication for the services (ADR 0009). On Azure it is Microsoft Entra ID: JWT bearer through Microsoft.Identity.Web,
/// configured under <c>AzureAd</c> (TenantId, ClientId, optional Audience). For local development there is a separate
/// dev scheme, which cannot be switched on outside the Development environment.
/// </summary>
public static class ClaimsIntakeAuthentication
{
    public const string ModeSetting = "Auth:Mode";
    public const string DevScheme = "DevAuth";

    /// <summary>
    /// Adds authentication and authorisation. Every endpoint requires an authenticated caller (the fallback policy) unless it
    /// opts out with <c>AllowAnonymous</c>. <paramref name="addPolicies"/> adds the service's own policies.
    /// </summary>
    public static void AddClaimsIntakeAuthentication(this WebApplicationBuilder builder, Action<AuthorizationOptions> addPolicies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(addPolicies);

        var mode = builder.Configuration[ModeSetting] is { Length: > 0 } m ? m.Trim() : "Entra";
        if (mode.Equals("Dev", StringComparison.OrdinalIgnoreCase))
        {
            RequireDevelopmentHost(builder);
            builder.Services.AddAuthentication(DevScheme).AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevScheme, _ => { });
        }
        else if (mode.Equals("Entra", StringComparison.OrdinalIgnoreCase))
        {
            AddEntra(builder);
        }
        else
        {
            throw new InvalidOperationException($"{ModeSetting} must be \"Entra\" or \"Dev\", not \"{mode}\".");
        }

        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            addPolicies(options);
        });
    }

    /// <summary>Policy: a person with a handler role, using a user token.</summary>
    public static void AddHandlerPolicy(this AuthorizationOptions options) =>
        options.AddPolicy(Policies.Handler, p => p.RequireAuthenticatedUser().RequireAssertion(c =>
            !c.User.IsAppOnly() && (c.User.HasAppRole(AppRoles.ClaimsHandler) || c.User.HasAppRole(AppRoles.SeniorHandler))));

    /// <summary>Policy: an app-only token that carries the given app role. A user token is refused even if it carries the role.</summary>
    public static void AddAppRolePolicy(this AuthorizationOptions options, string policy, string role) =>
        options.AddPolicy(policy, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.IsAppOnly() && c.User.HasAppRole(role)));

    private static void AddEntra(WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("AzureAd");
        if (string.IsNullOrWhiteSpace(section["TenantId"]) || string.IsNullOrWhiteSpace(section["ClientId"]))
        {
            throw new InvalidOperationException(
                "AzureAd:TenantId and AzureAd:ClientId are required. The service does not start without authentication configured. "
                + "For local development set Auth:Mode to Dev (Development environment only).");
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddMicrosoftIdentityWebApi(section);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            // Keep the claim names as the token has them (oid, name, roles, idtyp), and accept the app's own id as audience.
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "roles";
            var clientId = section["ClientId"]!;
            var audiences = new List<string> { clientId, $"api://{clientId}" };
            if (!string.IsNullOrWhiteSpace(section["Audience"]))
            {
                audiences.Add(section["Audience"]!);
            }

            options.TokenValidationParameters.ValidAudiences = audiences;
        });
    }

    /// <summary>
    /// The dev scheme needs the Development environment, and refuses to run on an Azure-hosted process even if somebody sets
    /// the environment name to Development there (App Service and Functions set these variables).
    /// </summary>
    private static void RequireDevelopmentHost(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{ModeSetting}=Dev is only allowed in the Development environment, not \"{builder.Environment.EnvironmentName}\".");
        }

        if (IsAzureHosted(builder.Configuration))
        {
            throw new InvalidOperationException($"{ModeSetting}=Dev is not allowed on an Azure-hosted process.");
        }
    }

    internal static bool IsAzureHosted(IConfiguration configuration) =>
        new[] { "WEBSITE_SITE_NAME", "WEBSITE_INSTANCE_ID", "FUNCTIONS_WORKER_RUNTIME", "CONTAINER_APP_NAME", "IDENTITY_ENDPOINT" }
            .Any(name => !string.IsNullOrWhiteSpace(configuration[name]));
}

/// <summary>
/// The development-only authentication scheme: <c>Authorization: Bearer dev-handler</c> and friends (see
/// <see cref="DevPersonas"/>). It is registered only when <c>Auth:Mode</c> is <c>Dev</c> in the Development environment, and
/// it checks the environment and the caller's address again on every request, as a second lock.
/// </summary>
public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment())
        {
            return Task.FromResult(AuthenticateResult.Fail("The dev scheme only works in Development."));
        }

        // A dev token is accepted from the machine itself only. No address (an in-memory test server) is allowed.
        if (Context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
        {
            return Task.FromResult(AuthenticateResult.Fail("The dev scheme only accepts local callers."));
        }

        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var persona = DevPersonas.Find(header["Bearer ".Length..].Trim());
        if (persona is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Not a dev token."));
        }

        var claims = new List<Claim> { new("oid", persona.ObjectId), new("name", persona.DisplayName) };
        claims.AddRange(persona.Roles.Select(r => new Claim("roles", r)));
        if (persona.IsApp)
        {
            claims.Add(new Claim("idtyp", "app"));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name, "name", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
