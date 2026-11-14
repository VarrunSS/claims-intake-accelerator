using ClaimsIntake.Domain;
using ClaimsIntake.Functions;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimsIntake.Pipeline.Tests;

public class CompositionTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaimIntakePipeline(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void With_no_settings_everything_is_local_and_needs_no_azure_account()
    {
        using var provider = Build();

        Assert.IsType<LocalPdfTextExtractor>(provider.GetRequiredService<IDocumentTextExtractor>());
        Assert.IsType<HeuristicClaimExtractor>(provider.GetRequiredService<IClaimExtractor>());
        Assert.IsType<HttpClaimSink>(provider.GetRequiredService<IClaimSink>());
        Assert.IsType<LegacyPolicyLookup>(provider.GetRequiredService<IPolicyLookup>());
        Assert.NotNull(provider.GetRequiredService<ClaimIntakePipeline>());
    }

    [Fact]
    public async Task The_azure_implementations_are_chosen_by_configuration_and_need_no_keys()
    {
        await using var provider = Build(
            ("Intake:TextExtractor", "DocumentIntelligence"),
            ("Intake:DocumentIntelligence:Endpoint", "https://di-example.cognitiveservices.azure.com/"),
            ("Intake:ClaimExtractor", "foundry"),
            ("Intake:Foundry:Endpoint", "https://example.openai.azure.com/"),
            ("Intake:Foundry:Deployment", "gpt-extraction"),
            ("Intake:Sink", "ServiceBus"),
            ("Intake:ServiceBus:FullyQualifiedNamespace", "sb-example.servicebus.windows.net"),
            ("Intake:ManagedIdentityClientId", "00000000-0000-0000-0000-000000000000"));

        Assert.IsType<AzureDocumentIntelligenceExtractor>(provider.GetRequiredService<IDocumentTextExtractor>());
        Assert.IsType<FoundryLlmClaimExtractor>(provider.GetRequiredService<IClaimExtractor>());
        Assert.IsType<ServiceBusClaimSink>(provider.GetRequiredService<IClaimSink>());
        Assert.NotNull(provider.GetRequiredService<IChatClient>());
        Assert.NotNull(provider.GetRequiredService<ClaimIntakePipeline>());
    }

    [Theory]
    [InlineData("Intake:TextExtractor", "Tesseract")]
    [InlineData("Intake:ClaimExtractor", "Guess")]
    [InlineData("Intake:Sink", "Email")]
    public void An_unknown_choice_is_refused_at_start_up(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build((key, value)));

        Assert.Contains(key, ex.Message);
    }

    [Theory]
    [InlineData("Intake:TextExtractor", "DocumentIntelligence", "Intake:DocumentIntelligence:Endpoint")]
    [InlineData("Intake:ClaimExtractor", "Foundry", "Intake:Foundry:Endpoint")]
    [InlineData("Intake:Sink", "ServiceBus", "Intake:ServiceBus:FullyQualifiedNamespace")]
    public void An_azure_choice_without_its_endpoint_is_refused_at_start_up(string key, string value, string missing)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build((key, value)));

        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Foundry_also_needs_a_deployment_name_and_endpoints_must_be_https()
    {
        Assert.Throws<InvalidOperationException>(() => Build(
            ("Intake:ClaimExtractor", "Foundry"), ("Intake:Foundry:Endpoint", "https://example.openai.azure.com/")));
        Assert.Throws<InvalidOperationException>(() => Build(
            ("Intake:TextExtractor", "DocumentIntelligence"), ("Intake:DocumentIntelligence:Endpoint", "http://di.example/")));
        Assert.Throws<InvalidOperationException>(() => Build(("Intake:Legacy:BaseUrl", "ftp://legacy.example")));
    }

    [Fact]
    public void Base_addresses_get_a_trailing_slash_so_relative_paths_resolve_under_them()
    {
        using var provider = Build(("Intake:Legacy:BaseUrl", "http://legacy.test:5200/base"));

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IPolicyLookup));

        Assert.Equal("http://legacy.test:5200/base/", client.BaseAddress!.ToString());
    }
}

public class ClaimDocumentReceivedTests
{
    [Fact]
    public async Task The_function_hands_the_blob_to_the_pipeline_under_its_inbox_name()
    {
        var sink = new RecordingSink();
        var function = new ClaimDocumentReceived(Docs.Pipeline(sink: sink));
        await using var blob = File.OpenRead(Path.Combine(Docs.SamplesFolder, "02-theft-clean.pdf"));

        await function.Process(blob, "02-theft-clean.pdf", null, CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        Assert.Equal("inbox/02-theft-clean.pdf", claim.DocumentId);
        Assert.Equal("TM-CP-100002", claim.Fields.Single(f => f.Name == "policy_number").Value);
    }

    [Fact]
    public async Task A_failing_document_throws_so_the_blob_trigger_retries_and_then_poisons_it()
    {
        var function = new ClaimDocumentReceived(Docs.Pipeline());
        await using var blob = new MemoryStream("hello"u8.ToArray());

        await Assert.ThrowsAsync<UnreadableDocumentException>(() => function.Process(blob, "claim.docx", null, CancellationToken.None));
    }

    [Fact]
    public async Task A_corrupt_pdf_is_handed_to_a_person_instead_of_poisoning_the_blob()
    {
        var sink = new RecordingSink();
        var function = new ClaimDocumentReceived(Docs.Pipeline(sink: sink));
        await using var blob = new MemoryStream("not a pdf"u8.ToArray());

        await function.Process(blob, "broken.pdf", null, CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        Assert.Equal(IssueCode.ProtectedOrCorruptDocument, Assert.Single(claim.Issues).Code);
    }

    [Fact]
    public async Task The_blobs_creation_time_is_the_received_time_passed_to_the_pipeline()
    {
        var sink = new RecordingSink();
        var function = new ClaimDocumentReceived(Docs.Pipeline(sink: sink));
        await using var blob = File.OpenRead(Path.Combine(Docs.SamplesFolder, "02-theft-clean.pdf"));
        var created = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

        await function.Process(blob, "02-theft-clean.pdf", created, CancellationToken.None);

        Assert.Equal(created, Assert.Single(sink.Claims).ReceivedAt);
    }

    [Fact]
    public async Task A_blob_larger_than_the_limit_is_refused_while_it_is_being_read()
    {
        await using var huge = new MemoryStream(new byte[ClaimIntakePipeline.HardLimitBytes + 1]);

        await Assert.ThrowsAsync<UnreadableDocumentException>(() => ClaimDocumentReceived.ReadLimitedAsync(huge, CancellationToken.None));
    }

    [Fact]
    public async Task A_blob_at_the_limit_is_read_in_full()
    {
        await using var exact = new MemoryStream(new byte[ClaimIntakePipeline.HardLimitBytes]);

        var data = await ClaimDocumentReceived.ReadLimitedAsync(exact, CancellationToken.None);

        Assert.Equal(ClaimIntakePipeline.HardLimitBytes, data.ToMemory().Length);
    }
}
