using ClaimsIntake.Api;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Domain;
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

    public Claim AlreadyRejected { get; } = Build("inbox/rejected.pdf", Now.AddHours(-4), []);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting reaches WebApplication.CreateBuilder's configuration; ConfigureAppConfiguration would not.
        builder.UseSetting("Telemetry:Exporter", "none");
        builder.UseSetting("Seed:DemoData", "false");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));

            services.RemoveAll<IClaimRepository>();
            AlreadyRejected.Reject("seed.handler", "Duplicate of an earlier claim.", Now.AddMinutes(-30));
            var repository = new InMemoryClaimRepository();
            foreach (var claim in new[] { Clean, WithWarning, WithError, AlreadyRejected })
            {
                repository.AddAsync(claim, CancellationToken.None).GetAwaiter().GetResult();
            }

            services.AddSingleton<IClaimRepository>(repository);
        });
    }

    private static Claim Build(string document, DateTimeOffset receivedAt, ValidationIssue[] issues) =>
        new(
            Guid.NewGuid(),
            document,
            receivedAt,
            [
                new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.98, new SourceSpan(document, 1, 10, 12)),
                new ExtractedField(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.85),
            ],
            issues);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
