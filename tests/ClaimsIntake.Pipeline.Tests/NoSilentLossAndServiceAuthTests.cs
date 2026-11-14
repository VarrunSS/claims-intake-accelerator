using System.Net;
using System.Text;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Text;
using ClaimsIntake.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaimsIntake.Pipeline.Tests;

/// <summary>REL-02 and REL-04 in the pipeline, and the token handlers on the typed clients (SEC-05, SEC-06).</summary>
public class NoSilentLossAndServiceAuthTests
{
    private sealed class PagesExtractor(IReadOnlyList<string> pages) : IDocumentTextExtractor
    {
        public Task<DocumentText> ExtractAsync(IntakeDocument document, CancellationToken cancellationToken) =>
            Task.FromResult(new DocumentText(pages));
    }

    private sealed class FakeSender : ServiceBusSender
    {
        public List<ServiceBusMessage> Sent { get; } = [];

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static string ClaimPage =>
        "Claim notification. Policy number: TM-CP-100001\nInsured: Brindle & Marsh Joinery Ltd\nDate of loss: 3 November 2026\n"
        + "A water pipe burst in the workshop store room overnight and damaged stock. Estimated amount: £12,500.";

    private static ProposedClaim Huge(int characters) =>
        new("inbox/huge.pdf", TestTime.Now, [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.9)], [],
            [new string('x', characters)], new ClaimAssessment("rules", [], ReviewRoute.StraightToHandler, "ok", []));

    // ----- REL-04: the pipeline enforces the limits the API checks -----

    [Fact]
    public async Task A_101_page_document_is_handed_off_cut_down_with_a_blocking_issue_not_lost()
    {
        var pages = new List<string> { ClaimPage };
        pages.AddRange(Enumerable.Range(0, IntakeLimits.MaxPages).Select(i => $"Page {i + 2} of the schedule."));
        var sink = new RecordingSink();
        var pipeline = new ClaimIntakePipeline(new PagesExtractor(pages), new HeuristicClaimExtractor(), FakePolicyLookup.Seeded(), sink,
            new FixedTimeProvider(TestTime.Now), new ListLogger<ClaimIntakePipeline>());

        var result = await pipeline.ProcessAsync(new IntakeDocument("inbox/long.txt", "long.txt", BinaryData.FromString(ClaimPage)), CancellationToken.None);

        var proposed = Assert.Single(sink.Claims);
        Assert.Equal(101, pages.Count);
        Assert.Equal(IntakeLimits.MaxPages, proposed.SourcePages.Count);
        Assert.Contains(proposed.Issues, i => i.Code == IssueCode.DocumentTooLarge && i.Severity == IssueSeverity.Error);
        Assert.Equal(ReviewRoute.SeniorReview, proposed.Assessment?.Route);
        Assert.True(result.ErrorCount > 0);
    }

    // ----- REL-02: the Service Bus size guard -----

    [Fact]
    public async Task A_300_KB_claim_is_cut_to_fit_under_the_service_bus_limit_before_it_is_sent()
    {
        var sender = new FakeSender();
        var claim = Huge(300 * 1024);

        await new ServiceBusClaimSink(sender).HandOffAsync(claim, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.True(message.Body.ToMemory().Length <= IntakeLimits.MaxHandoffBytes, $"{message.Body.ToMemory().Length} bytes");
        Assert.True(message.Body.ToMemory().Length < 256 * 1024);
        var received = ProposedClaim.FromJson(message.Body.ToString());
        Assert.Equal(claim.ClaimId, received.ClaimId);
        Assert.Equal(message.MessageId, claim.ClaimId.ToString("D"));
        Assert.Contains(received.Issues, i => i.Code == IssueCode.DocumentTooLarge && i.Message.Contains("original document"));
        Assert.True(received.ToClaim().HasBlockingIssues);
    }

    [Fact]
    public async Task A_claim_that_fits_is_sent_unchanged()
    {
        var sender = new FakeSender();
        var claim = Huge(1000);

        await new ServiceBusClaimSink(sender).HandOffAsync(claim, CancellationToken.None);

        Assert.Equal(claim.ToJson(), Assert.Single(sender.Sent).Body.ToString());
    }

    // ----- SEC-05 / SEC-06: tokens on the typed clients -----

    private sealed class FakeCredential(Func<DateTimeOffset> expiry) : TokenCredential
    {
        public int Calls { get; private set; }

        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => Issue(requestContext);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(Issue(requestContext));

        private AccessToken Issue(TokenRequestContext context)
        {
            Calls++;
            Scopes.AddRange(context.Scopes);
            return new AccessToken($"token-{Calls}", expiry());
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task The_app_token_handler_attaches_a_bearer_token_for_the_scope_and_caches_it_until_shortly_before_expiry()
    {
        var clock = new MovableClock(TestTime.Now);
        var credential = new FakeCredential(() => clock.Now.AddHours(1));
        var inner = new CapturingHandler();
        using var client = new HttpClient(new AppTokenHandler(credential, "api://claims-intake-legacy/.default", clock) { InnerHandler = inner });

        await client.GetAsync("http://legacy.test/policies/x");
        await client.GetAsync("http://legacy.test/policies/y");
        Assert.Equal(1, credential.Calls);
        Assert.Equal(["Bearer token-1", "Bearer token-1"], inner.Authorizations);
        Assert.Equal(["api://claims-intake-legacy/.default"], credential.Scopes);

        clock.Now = clock.Now.AddMinutes(58); // Within five minutes of expiry: fetch a new one.
        await client.GetAsync("http://legacy.test/policies/z");

        Assert.Equal(2, credential.Calls);
        Assert.Equal("Bearer token-2", inner.Authorizations[^1]);
    }

    [Fact]
    public async Task The_dev_token_handler_sends_the_persona_token_in_development()
    {
        var inner = new CapturingHandler();
        var handler = new DevTokenHandler(DevPersonas.Pipeline, new TestEnvironment(Environments.Development)) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        await client.GetAsync("http://legacy.test/claims");

        Assert.Equal(["Bearer dev-app-pipeline"], inner.Authorizations);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void The_dev_token_handler_cannot_be_built_outside_development(string environment)
    {
        Assert.Throws<InvalidOperationException>(() => new DevTokenHandler(DevPersonas.Pipeline, new TestEnvironment(environment)));
    }

    [Fact]
    public async Task Configuration_chooses_the_azure_scope_over_a_dev_persona_and_sends_nothing_when_neither_is_set()
    {
        var azure = await AuthorizationViaExtension("api://legacy/.default", null, Environments.Production);
        var dev = await AuthorizationViaExtension(null, "dev-app-pipeline", Environments.Development);
        var none = await AuthorizationViaExtension(null, null, Environments.Production);

        Assert.Equal("Bearer token-1", azure);
        Assert.Equal("Bearer dev-app-pipeline", dev);
        Assert.Null(none);
    }

    private static async Task<string?> AuthorizationViaExtension(string? scope, string? persona, string environment)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        services.AddSingleton<TokenCredential>(new FakeCredential(() => TestTime.Now.AddHours(1)));
        var capture = new CapturingHandler();
        services.AddHttpClient("legacy").ConfigurePrimaryHttpMessageHandler(() => capture)
            .AddServiceAuthentication(scope, persona);
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IHttpClientFactory>().CreateClient("legacy").GetAsync("http://legacy.test/policies/x");

        return capture.Authorizations.Single();
    }

    [Fact]
    public async Task A_dev_persona_cannot_be_used_in_production_even_if_configured()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(Environments.Production));
        services.AddHttpClient("legacy").ConfigurePrimaryHttpMessageHandler(() => new CapturingHandler())
            .AddServiceAuthentication(null, "dev-app-pipeline");
        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient("legacy").GetAsync("http://legacy.test/policies/x"));
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
