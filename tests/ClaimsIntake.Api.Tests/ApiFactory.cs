using ClaimsIntake.Api;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Legacy;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaimsIntake.Api.Tests;

/// <summary>Runs the API in memory with a known set of claims and no telemetry output.</summary>
public sealed class ApiFactory : WebApplicationFactory<ApiMarker>
{
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    public Claim Clean { get; } = Build("inbox/clean.pdf", Now.AddHours(-3), []);

    public Claim WithWarning { get; } = Build("inbox/warning.pdf", Now.AddHours(-2),
        [new(IssueCode.LowConfidence, IssueSeverity.Warning, ClaimFields.ClaimantName, "Low confidence.")]);

    public Claim WithError { get; } = Build("inbox/error.pdf", Now.AddHours(-1),
        [new(IssueCode.MissingPolicyNumber, IssueSeverity.Error, ClaimFields.PolicyNumber, "No policy number.")]);

    /// <summary>Routed to senior review by the assessor: only a SeniorHandler may approve it (SEC-03).</summary>
    public Claim SeniorRouted { get; } = BuildRouted("inbox/senior.pdf", Now.AddHours(-5), ReviewRoute.SeniorReview);

    /// <summary>Routed to a handler by the assessor.</summary>
    public Claim HandlerRouted { get; } = BuildRouted("inbox/handler.pdf", Now.AddHours(-6), ReviewRoute.StraightToHandler);

    /// <summary>The hosting environment. Development by default, which is where the local hand-off route exists.</summary>
    public string Environment { get; init; } = "Development";

    public InMemoryClaimRepository Repository { get; private set; } = null!;

    /// <summary>Where decisions are audited. The in-memory log, so tests can read the rows.</summary>
    public InMemoryAuditLog Audit { get; } = new();

    public Claim AlreadyRejected { get; } = Build("inbox/rejected.pdf", Now.AddHours(-4), []);

    /// <summary>Stands in for the legacy system so these tests control what it answers and count its calls.</summary>
    public FakeLegacyWriter Legacy { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        // UseSetting reaches WebApplication.CreateBuilder's configuration; ConfigureAppConfiguration would not.
        builder.UseSetting("Telemetry:Exporter", "none");
        builder.UseSetting("Seed:DemoData", "false");
        builder.UseSetting("Intake:HttpEndpoint:Enabled", "true");
        // The real Entra pipeline (Microsoft.Identity.Web), with test-signed tokens (TestAuth).
        builder.UseSetting("Auth:Mode", "Entra");
        builder.UseSetting("AzureAd:TenantId", TestAuth.TenantId);
        builder.UseSetting("AzureAd:ClientId", TestAuth.ApiClientId);

        builder.ConfigureServices(services =>
        {
            services.UseTestSigning(TestAuth.ApiClientId);
            services.RemoveAll<IAuditLog>();
            services.AddSingleton<IAuditLog>(Audit);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));

            services.RemoveAll<ILegacyClaimWriter>();
            services.AddSingleton<ILegacyClaimWriter>(Legacy);

            services.RemoveAll<IClaimRepository>();
            AlreadyRejected.Reject(new ClaimsIntake.Domain.Security.Approver("seed-handler", "Seed", [ClaimsIntake.Domain.Security.AppRoles.ClaimsHandler]), "Duplicate of an earlier claim.", Now.AddMinutes(-30));
            var repository = new InMemoryClaimRepository();
            foreach (var claim in new[] { Clean, WithWarning, WithError, AlreadyRejected })
            {
                repository.AddAsync(claim, CancellationToken.None).GetAwaiter().GetResult();
            }

            Repository = repository;
            services.AddSingleton<IClaimRepository>(repository);
        });
    }

    /// <summary>Every client signs in as a ClaimsHandler unless a test says otherwise (<c>client.WithBearer(...)</c>).</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.WithBearer(TestAuth.Handler());
    }

    /// <summary>Adds the claims that are routed by the assessor to the store. Call before the first request.</summary>
    public ApiFactory WithRoutedClaims()
    {
        _ = Server; // Builds the host, and with it the repository.
        Repository.AddAsync(SeniorRouted, CancellationToken.None).GetAwaiter().GetResult();
        Repository.AddAsync(HandlerRouted, CancellationToken.None).GetAwaiter().GetResult();
        return this;
    }

    private static Claim BuildRouted(string document, DateTimeOffset receivedAt, ReviewRoute route)
    {
        var claim = Build(document, receivedAt, []);
        claim.AttachAssessment(new ClaimAssessment("rules", [], route, "Because.", []));
        return claim;
    }

    private static Claim Build(string document, DateTimeOffset receivedAt, ValidationIssue[] issues) =>
        new(
            Guid.NewGuid(),
            document,
            receivedAt,
            [
                new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.98, new SourceSpan(document, 1, 10, 12)),
                new ExtractedField(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.85),
                new ExtractedField(ClaimFields.LossDate, "2026-11-03", 0.93),
                new ExtractedField(ClaimFields.LossDescription, "Burst pipe in the workshop.", 0.9),
                new ExtractedField(ClaimFields.EstimatedAmount, "£12,500", 0.9),
            ],
            issues,
            ["Policy number: TM-CP-100001\nInsured: Brindle & Marsh Joinery Ltd"]);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>A legacy writer that behaves like the real one: the same claim id always gets the same reference.</summary>
public sealed class FakeLegacyWriter : ILegacyClaimWriter
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, string> _references = [];

    public int Calls { get; private set; }
    public int Created => _references.Count;
    public List<Guid> KeysSeen { get; } = [];

    /// <summary>When set, the next calls throw this instead of answering. Cleared by setting it back to null.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>
    /// When true, the claim is stored by the "legacy system" and then the call fails, as when a response is lost
    /// to a timeout. The caller cannot tell whether the claim was created.
    /// </summary>
    public bool LoseResponse { get; set; }

    public async Task<LegacySubmission> SubmitAsync(Claim claim, CancellationToken cancellationToken)
    {
        await Task.Delay(20, cancellationToken); // Gives concurrent callers time to overlap.
        lock (_gate)
        {
            Calls++;
            KeysSeen.Add(claim.Id);
            if (FailWith is not null)
            {
                throw FailWith;
            }

            var replayed = _references.TryGetValue(claim.Id, out var reference);
            if (!replayed)
            {
                reference = $"LC-{_references.Count + 1:D6}";
                _references[claim.Id] = reference;
            }

            if (LoseResponse)
            {
                throw new LegacyUnavailableException("The response was lost.");
            }

            return new LegacySubmission(reference!, replayed);
        }
    }
}
