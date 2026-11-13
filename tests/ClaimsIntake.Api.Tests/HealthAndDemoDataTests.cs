using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Domain;
using Microsoft.AspNetCore.Hosting;

namespace ClaimsIntake.Api.Tests;

public class HealthAndDemoDataTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiFactory _factory;

    public HealthAndDemoDataTests(ApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_report_healthy(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void An_unsupported_telemetry_exporter_stops_the_app_at_start_up()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Telemetry:Exporter", "carrier-pigeon"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("carrier-pigeon", ex.Message);
    }

    [Fact]
    public void Demo_data_covers_a_clean_claim_and_each_blocking_rule()
    {
        var claims = DemoData.Create(TimeProvider.System);

        Assert.Contains(claims, c => c.Issues.Count == 0);
        Assert.Contains(claims, c => c.Issues.Any(i => i.Code == IssueCode.MissingPolicyNumber));
        Assert.Contains(claims, c => c.Issues.Any(i => i.Code == IssueCode.LossDateInFuture));
        Assert.Contains(claims, c => c.Issues.Any(i => i.Code == IssueCode.UnreadableLossDate));
        Assert.Contains(claims, c => c.Issues.Any(i => i.Code == IssueCode.LowConfidence));
        Assert.Equal(claims.Count, claims.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public async Task Demo_data_is_loaded_only_when_the_seed_setting_is_on()
    {
        using var seeded = new ApiFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Seed:DemoData", "true"));

        var claims = await seeded.CreateClient().GetFromJsonAsync<List<ClaimSummary>>("/claims?status=all", Json);

        // The test repository holds four claims; the demo data adds more on top of them.
        Assert.True(claims!.Count > 4);
    }
}
