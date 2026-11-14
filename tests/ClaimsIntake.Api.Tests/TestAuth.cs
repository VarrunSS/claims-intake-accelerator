using System.Net.Http.Headers;
using ClaimsIntake.Domain.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ClaimsIntake.Api.Tests;

/// <summary>
/// Test-signed JWTs for the real authentication pipeline. The services run Microsoft.Identity.Web exactly as in Azure, and
/// only the signing keys and issuer metadata are replaced with a symmetric test key, so no network is needed. Everything
/// else (audience and issuer validation, lifetime, role claims, the policies) is the production code path.
/// </summary>
public static class TestAuth
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string Issuer = $"https://login.test/{TenantId}/v2.0";

    public const string ApiClientId = "22222222-2222-2222-2222-222222222222";
    public const string LegacyClientId = "33333333-3333-3333-3333-333333333333";

    public const string HandlerOid = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string SeniorOid = "aaaaaaaa-0000-0000-0000-000000000002";

    private static readonly SymmetricSecurityKey SigningKey = new(Convert.FromHexString(
        "6b6579732d666f722d7465737473206f6e6c792d6e6f742d612d736563726574206b6579206f6e6c79"));

    /// <summary>Replaces the signing keys and issuer metadata of the JWT bearer scheme with the test key.</summary>
    public static void UseTestSigning(this IServiceCollection services, string audience) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(SigningKey);
            options.Authority = null;
            options.MetadataAddress = null!;
            options.Configuration = configuration;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudiences = [audience, $"api://{audience}"],
                IssuerSigningKey = SigningKey,
                NameClaimType = "name",
                RoleClaimType = "roles",
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
            };
        });

    /// <summary>A signed token. <paramref name="app"/> makes it an app-only token (<c>idtyp=app</c>, no name), as client-credentials tokens are.</summary>
    public static string Issue(
        string audience, IEnumerable<string> roles, string oid = HandlerOid, string? name = "A Patel", bool app = false,
        DateTime? expires = null, SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object> { ["oid"] = oid, ["roles"] = roles.ToArray() };
        if (name is not null && !app)
        {
            claims["name"] = name;
        }

        if (app)
        {
            claims["idtyp"] = "app";
        }

        var now = DateTime.UtcNow;
        var expiry = expires ?? now.AddHours(1);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = claims,
            Expires = expiry,
            NotBefore = expiry < now ? expiry.AddMinutes(-10) : now.AddMinutes(-5),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static string Handler(string audience = ApiClientId) => Issue(audience, [AppRoles.ClaimsHandler], HandlerOid, "A Patel");

    public static string Senior(string audience = ApiClientId) => Issue(audience, [AppRoles.SeniorHandler], SeniorOid, "B Jones");

    public const string PipelineOid = "bbbbbbbb-0000-0000-0000-000000000001";

    /// <summary>The intake pipeline's app-only identity: reads the review history and hands claims over.</summary>
    public static string Pipeline(string audience = ApiClientId) =>
        Issue(audience, [AppRoles.IntakeReadHistory, AppRoles.IntakeSubmit], PipelineOid, name: null, app: true);

    /// <summary>POSTs to the local hand-off route with the pipeline's app token, whatever the client is signed in as.</summary>
    public static Task<HttpResponseMessage> PostIntakeAsync(this HttpClient client, object body) =>
        client.PostIntakeAsync(System.Net.Http.Json.JsonContent.Create(body, mediaType: null, options: ClaimsIntake.Domain.Intake.ProposedClaim.JsonOptions));

    public static Task<HttpResponseMessage> PostIntakeAsync(this HttpClient client, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/intake/claims") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Pipeline());
        return client.SendAsync(request);
    }

    public static HttpClient WithBearer(this HttpClient client, string? token)
    {
        client.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>
/// The legacy mock in memory, with the real Entra pipeline and test-signed tokens. Its default client carries an app-only token
/// with all three legacy roles; tests that check refusals replace it (<c>WithBearer</c>).
/// </summary>
public sealed class LegacyFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ClaimsIntake.Legacy.LegacyMarker>
{
    public static string App(params string[] roles) => TestAuth.Issue(TestAuth.LegacyClientId, roles, TestAuth.PipelineOid, name: null, app: true);

    public static string AllRoles() => App(AppRoles.ClaimsWrite, AppRoles.ClaimsRead, AppRoles.PoliciesRead);

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Auth:Mode", "Entra");
        builder.UseSetting("AzureAd:TenantId", TestAuth.TenantId);
        builder.UseSetting("AzureAd:ClientId", TestAuth.LegacyClientId);
        builder.ConfigureServices(services => services.UseTestSigning(TestAuth.LegacyClientId));
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.WithBearer(AllRoles());
    }
}
