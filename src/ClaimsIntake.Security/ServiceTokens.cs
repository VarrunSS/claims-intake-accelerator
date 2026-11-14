using System.Net.Http.Headers;
using Azure.Core;
using ClaimsIntake.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ClaimsIntake.Security;

/// <summary>
/// A fixed identity for local development (ADR 0009). The token is the persona's name, for example <c>dev-handler</c>. It is
/// only ever understood by the Development-only dev authentication scheme, so it is worthless anywhere else.
/// </summary>
public sealed record DevPersona(string Token, string ObjectId, string DisplayName, IReadOnlyList<string> Roles, bool IsApp);

/// <summary>The personas the dev scheme knows: two people and the two service identities.</summary>
public static class DevPersonas
{
    public static readonly DevPersona Handler = new("dev-handler", "00000000-0000-0000-0000-0000000000a1", "Dev Handler", [AppRoles.ClaimsHandler], false);

    public static readonly DevPersona Senior = new("dev-senior", "00000000-0000-0000-0000-0000000000a2", "Dev Senior Handler", [AppRoles.SeniorHandler], false);

    /// <summary>The intake pipeline (inbox watcher, Function): reads policies and claims, reads the review history, hands claims over.</summary>
    public static readonly DevPersona Pipeline = new("dev-app-pipeline", "00000000-0000-0000-0000-0000000000b1", "intake-pipeline",
        [AppRoles.PoliciesRead, AppRoles.ClaimsRead, AppRoles.IntakeReadHistory, AppRoles.IntakeSubmit], true);

    /// <summary>The review API: the only identity that may write a claim to the legacy system.</summary>
    public static readonly DevPersona ReviewApi = new("dev-app-api", "00000000-0000-0000-0000-0000000000b2", "review-api", [AppRoles.ClaimsWrite], true);

    public static IReadOnlyList<DevPersona> All { get; } = [Handler, Senior, Pipeline, ReviewApi];

    public static DevPersona? Find(string? token) =>
        All.FirstOrDefault(p => string.Equals(p.Token, token, StringComparison.Ordinal));
}

/// <summary>
/// Adds an app-only bearer token to each outgoing request, got from a <see cref="TokenCredential"/> for one scope (for
/// example <c>api://claims-intake-legacy/.default</c>). Used on Azure with <c>DefaultAzureCredential</c>, which is a managed
/// identity there. The token is cached until shortly before it expires.
/// </summary>
public sealed class AppTokenHandler(TokenCredential credential, string scope, TimeProvider? time = null) : DelegatingHandler
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken _token;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrEmpty(_token.Token) || _token.ExpiresOn - _time.GetUtcNow() <= RefreshMargin)
            {
                _token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            }

            return _token.Token;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Local development only: sends a dev persona's token. It cannot be built outside the Development environment, so a
/// misconfigured deployment fails to start its first call rather than sending a dev token.
/// </summary>
public sealed class DevTokenHandler : DelegatingHandler
{
    private readonly DevPersona _persona;

    public DevTokenHandler(DevPersona persona, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Dev service tokens are only available in the Development environment.");
        }

        _persona = persona;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _persona.Token);
        return base.SendAsync(request, cancellationToken);
    }
}

public static class ServiceAuthenticationExtensions
{
    /// <summary>
    /// Authenticates calls from a typed client to another service. With a <paramref name="scope"/> it gets app-only tokens
    /// from the <see cref="TokenCredential"/> in the container (the managed identity on Azure). Otherwise, with a
    /// <paramref name="devPersona"/> token name, it sends that dev token (Development only). With neither it sends nothing,
    /// and the called service answers 401. Add it after the resilience handler so that each retry carries a token.
    /// </summary>
    public static IHttpClientBuilder AddServiceAuthentication(this IHttpClientBuilder builder, string? scope, string? devPersona)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!string.IsNullOrWhiteSpace(scope))
        {
            builder.Services.TryAddSingleton<TokenCredential>(_ => new Azure.Identity.DefaultAzureCredential());
            return builder.AddHttpMessageHandler(sp => new AppTokenHandler(sp.GetRequiredService<TokenCredential>(), scope.Trim(), sp.GetService<TimeProvider>()));
        }

        if (!string.IsNullOrWhiteSpace(devPersona))
        {
            var persona = DevPersonas.Find(devPersona.Trim())
                ?? throw new InvalidOperationException($"\"{devPersona}\" is not a dev persona.");
            return builder.AddHttpMessageHandler(sp => new DevTokenHandler(persona, sp.GetRequiredService<IHostEnvironment>()));
        }

        return builder;
    }
}
