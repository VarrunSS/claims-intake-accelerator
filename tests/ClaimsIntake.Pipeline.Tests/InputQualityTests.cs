using System.Text;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Pipeline.Quality;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaimsIntake.Pipeline.Tests;

public class InputQualityTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static InputQualityContext Doc(string name, byte[] bytes, DocumentText? text = null, string? id = null) =>
        new(new IntakeDocument(id ?? $"inbox/{name}", name, BinaryData.FromBytes(bytes)), text);

    private static InputQualityContext TextDoc(string text, string name = "x.txt") =>
        Doc(name, Encoding.UTF8.GetBytes(text), new DocumentText([text]));

    private static Task<QualityFinding?> Run(IInputQualityCheck check, InputQualityContext context) =>
        check.CheckAsync(context, CancellationToken.None);

    // --- Size ---

    [Fact]
    public async Task An_empty_document_is_unreadable() =>
        Assert.Equal(IssueCode.UnreadableDocument, (await Run(new DocumentSizeCheck(100), Doc("e.txt", [])))!.Code);

    [Fact]
    public async Task A_document_over_the_limit_is_too_large_and_one_at_the_limit_is_not()
    {
        var check = new DocumentSizeCheck(100);

        Assert.Equal(IssueCode.DocumentTooLarge, (await Run(check, Doc("big.txt", new byte[101])))!.Code);
        Assert.Null(await Run(check, Doc("ok.txt", new byte[100])));
    }

    // --- PDF integrity ---

    [Fact]
    public async Task A_file_that_is_not_a_pdf_but_is_named_pdf_is_corrupt_and_the_message_does_not_quote_it()
    {
        var finding = await Run(new PdfIntegrityCheck(), Doc("bad.pdf", Encoding.UTF8.GetBytes("SECRET-CLAIM-TEXT is not a pdf")));

        Assert.Equal(IssueCode.ProtectedOrCorruptDocument, finding!.Code);
        Assert.DoesNotContain("SECRET", finding.Message);
    }

    [Fact]
    public async Task A_truncated_pdf_is_corrupt()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Docs.SamplesFolder, "01-water-damage-clean.pdf"));

        var finding = await Run(new PdfIntegrityCheck(), Doc("cut.pdf", bytes[..(bytes.Length / 3)]));

        Assert.Equal(IssueCode.ProtectedOrCorruptDocument, finding!.Code);
    }

    [Fact]
    public async Task A_good_pdf_and_a_text_file_pass()
    {
        var pdf = File.ReadAllBytes(Path.Combine(Docs.SamplesFolder, "01-water-damage-clean.pdf"));

        Assert.Null(await Run(new PdfIntegrityCheck(), Doc("a.pdf", pdf)));
        Assert.Null(await Run(new PdfIntegrityCheck(), Doc("a.txt", Encoding.UTF8.GetBytes("not a pdf, and does not need to be"))));
    }

    [Fact]
    public async Task A_password_protected_pdf_is_reported_as_protected()
    {
        // fixtures/protected.pdf has a user password (RC4, written by evals/generate_dataset.py).
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures, "protected.pdf"));

        var finding = await Run(new PdfIntegrityCheck(), Doc("locked.pdf", bytes));

        Assert.Equal(IssueCode.ProtectedOrCorruptDocument, finding!.Code);
        Assert.Contains("password", finding.Message);
    }

    // --- Duplicate file ---

    [Fact]
    public async Task The_same_bytes_under_another_name_are_a_duplicate_but_a_retry_under_the_same_id_is_not()
    {
        var check = new DuplicateFileCheck(new InMemoryDocumentHashStore());
        var bytes = Encoding.UTF8.GetBytes("the same file");

        Assert.Null(await Run(check, Doc("a.txt", bytes)));
        Assert.Null(await Run(check, Doc("a.txt", bytes))); // The same document id again: a redelivery.
        var finding = await Run(check, Doc("copy.txt", bytes));

        Assert.Equal(IssueCode.DuplicateDocument, finding!.Code);
        Assert.Contains("inbox/a.txt", finding.Message);
        Assert.Null(await Run(check, Doc("other.txt", Encoding.UTF8.GetBytes("a different file"))));
    }

    // --- Readable text ---

    [Theory]
    [InlineData("")]
    [InlineData("   \n . . . \n")]
    [InlineData("See attached.")]
    [InlineData("-- 1 -- ~~ ..")]
    public async Task Little_or_no_text_is_unreadable(string text) =>
        Assert.Equal(IssueCode.UnreadableDocument, (await Run(new ReadableTextCheck(40), TextDoc(text)))!.Code);

    [Fact]
    public async Task Enough_text_is_readable() =>
        Assert.Null(await Run(new ReadableTextCheck(40), TextDoc("Policy number TM-CP-100001, a pipe burst in the store room overnight.")));

    // --- Claim content ---

    [Fact]
    public async Task A_marketing_email_is_not_a_claim()
    {
        var finding = await Run(new ClaimContentCheck(3), TextDoc(
            "Subject: 20% off business insurance this month only!\n\nLimited time offer for small firms. Get a quote today, click here to switch and save. " +
            "To unsubscribe from our newsletter, reply STOP."));

        Assert.Equal(IssueCode.NotAClaim, finding!.Code);
        Assert.Contains("marketing", finding.Message);
    }

    [Theory]
    [InlineData("Bonjour, nous avons eu un sinistre dans notre entrepôt la semaine dernière et nous voudrions savoir comment procéder avec votre assurance pour les dégâts causés par l'eau, merci de nous répondre.")]
    [InlineData("Guten Tag, in unserem Lager ist letzte Woche ein Wasserschaden entstanden und wir möchten wissen, wie wir bei Ihrer Versicherung vorgehen sollen, bitte melden Sie sich bei uns zurück.")]
    public async Task A_message_in_another_language_is_flagged_as_not_english(string text)
    {
        var finding = await Run(new ClaimContentCheck(3), TextDoc(text));

        Assert.Equal(IssueCode.NotAClaim, finding!.Code);
        Assert.Contains("English", finding.Message);
    }

    [Fact]
    public async Task English_text_with_nothing_about_a_claim_is_not_a_claim()
    {
        var finding = await Run(new ClaimContentCheck(3), TextDoc(
            "Hi all, the team lunch on Friday is moved to one o'clock because the room is booked for a meeting in the morning. Please let me know what you would like to eat."));

        Assert.Equal(IssueCode.NotAClaim, finding!.Code);
    }

    [Theory]
    [InlineData("Policy number: TM-CP-100001\nA water pipe burst in the store room and damaged timber stock. Estimated amount: £12,500")]
    [InlineData("Hi, there was a fire in our office last night, policy TM-CP-100006. Roughly £6,000 of damage.")]
    [InlineData("CLAIM NOTIF\nICATION  ref 77-B\nTM-CP-100007\n11/11/2026 damaged\nWalk-in freezer comp-\nressor failed overnight, stock\nspoiled £3 1OO")]
    public async Task Genuine_claims_pass_even_when_short_or_messy(string text) =>
        Assert.Null(await Run(new ClaimContentCheck(3), TextDoc(text)));

    [Fact]
    public async Task A_claim_that_mentions_an_offer_still_passes_when_it_has_strong_claim_evidence() =>
        Assert.Null(await Run(new ClaimContentCheck(3), TextDoc(
            "Policy TM-CP-100001. Fire damaged our premises on 3 November 2026, estimated loss £9,000. Please unsubscribe us from your newsletter too.")));

    // --- The gate ---

    private sealed class Finds(QualityStage stage, string name) : IInputQualityCheck
    {
        public int Calls { get; private set; }
        public string Name => name;
        public QualityStage Stage => stage;

        public Task<QualityFinding?> CheckAsync(InputQualityContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<QualityFinding?>(new(IssueCode.NotAClaim, name, name));
        }
    }

    [Fact]
    public async Task The_gate_runs_only_the_checks_of_the_stage_and_stops_at_the_first_finding()
    {
        var first = new Finds(QualityStage.Document, "first");
        var second = new Finds(QualityStage.Document, "second");
        var text = new Finds(QualityStage.Text, "text");
        var gate = new InputQualityGate([first, second, text]);

        var finding = await gate.RunAsync(QualityStage.Document, Doc("a.txt", [1]), CancellationToken.None);

        Assert.Equal("first", finding!.Check);
        Assert.Equal((1, 0, 0), (first.Calls, second.Calls, text.Calls));
    }

    [Fact]
    public void The_default_gate_has_the_five_checks_in_a_sensible_order() =>
        Assert.Equal(["document_size", "pdf_integrity", "duplicate_file", "readable_text", "claim_content"], InputQualityGate.CreateDefault().CheckNames);

    // --- In the pipeline ---

    private static ClaimIntakePipeline Pipeline(RecordingSink sink, InputQualityGate? gate = null, PolicyLookupSpy? policies = null, ClaimExtractorSpy? extractor = null) =>
        new(new LocalPdfTextExtractor(), extractor ?? new ClaimExtractorSpy(), policies ?? new PolicyLookupSpy(), sink,
            new FixedTimeProvider(TestTime.Now), NullLogger<ClaimIntakePipeline>.Instance, quality: gate);

    private sealed class PolicyLookupSpy : ClaimsIntake.Pipeline.Legacy.IPolicyLookup
    {
        public int Calls { get; private set; }

        public Task<PolicyReference?> FindAsync(string policyNumber, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<PolicyReference?>(null);
        }
    }

    private sealed class ClaimExtractorSpy : ClaimsIntake.Pipeline.Fields.IClaimExtractor
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ExtractedField>> ExtractAsync(string documentId, DocumentText text, CancellationToken cancellationToken)
        {
            Calls++;
            return new ClaimsIntake.Pipeline.Fields.HeuristicClaimExtractor().ExtractAsync(documentId, text, cancellationToken);
        }
    }

    [Fact]
    public async Task A_document_that_fails_a_check_reaches_the_sink_as_a_claim_for_a_person_and_nothing_is_extracted()
    {
        var sink = new RecordingSink();
        var extractor = new ClaimExtractorSpy();
        var policies = new PolicyLookupSpy();

        var result = await Pipeline(sink, policies: policies, extractor: extractor)
            .ProcessAsync(Docs.Text("promo.txt", "20% off insurance! Limited time offer, click here. Unsubscribe from our newsletter at any time."), CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        var issue = Assert.Single(claim.Issues);
        Assert.Equal((IssueCode.NotAClaim, IssueSeverity.Error), (issue.Code, issue.Severity));
        Assert.All(claim.Fields, f => Assert.False(f.HasValue));
        Assert.Equal(ReviewRoute.SeniorReview, claim.Assessment!.Route);
        Assert.Equal(ClaimIntakePipeline.InputQualityAssessor, claim.Assessment.Assessor);
        Assert.Contains("marketing", claim.Assessment.RouteReason);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal((0, 0), (extractor.Calls, policies.Calls));
        Assert.Single(claim.SourcePages); // A person can read what it was.
        Assert.True(claim.ToClaim().HasBlockingIssues); // So it can be rejected but never approved.
    }

    [Fact]
    public async Task A_scan_with_no_text_is_routed_to_a_person_not_failed()
    {
        // The PDF has pages but no text, like a scan: the extractor says NoText and the pipeline turns that into a review item.
        var sink = new RecordingSink();
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures, "scan.pdf"));

        await Pipeline(sink).ProcessAsync(new IntakeDocument("inbox/scan.pdf", "scan.pdf", BinaryData.FromBytes(bytes)), CancellationToken.None);

        Assert.Equal(IssueCode.UnreadableDocument, Assert.Single(Assert.Single(sink.Claims).Issues).Code);
    }

    [Fact]
    public async Task The_same_file_sent_twice_under_two_names_is_a_duplicate_document_the_second_time()
    {
        var sink = new RecordingSink();
        var pipeline = Pipeline(sink, InputQualityGate.CreateDefault());
        var bytes = File.ReadAllBytes(Path.Combine(Docs.SamplesFolder, "01-water-damage-clean.pdf"));

        await pipeline.ProcessAsync(new IntakeDocument("inbox/a.pdf", "a.pdf", BinaryData.FromBytes(bytes)), CancellationToken.None);
        await pipeline.ProcessAsync(new IntakeDocument("inbox/b.pdf", "b.pdf", BinaryData.FromBytes(bytes)), CancellationToken.None);

        Assert.DoesNotContain(sink.Claims[0].Issues, i => i.Code == IssueCode.DuplicateDocument);
        Assert.Equal(IssueCode.DuplicateDocument, Assert.Single(sink.Claims[1].Issues).Code);
    }

    [Fact]
    public async Task The_limits_can_be_configured()
    {
        var sink = new RecordingSink();
        var gate = InputQualityGate.CreateDefault(new InputQualityOptions { MaxDocumentBytes = 50 });

        await Pipeline(sink, gate).ProcessAsync(Docs.Text("a.txt", new string('x', 51)), CancellationToken.None);

        Assert.Equal(IssueCode.DocumentTooLarge, Assert.Single(Assert.Single(sink.Claims).Issues).Code);
    }

    [Fact]
    public async Task Every_clean_sample_passes_the_default_gate()
    {
        foreach (var sample in Directory.EnumerateFiles(Docs.SamplesFolder).Select(Path.GetFileName).Order())
        {
            var sink = new RecordingSink();

            await Pipeline(sink).ProcessAsync(Docs.Sample(sample!), CancellationToken.None);

            Assert.DoesNotContain(Assert.Single(sink.Claims).Issues,
                i => i.Code is IssueCode.UnreadableDocument or IssueCode.NotAClaim or IssueCode.ProtectedOrCorruptDocument or IssueCode.DocumentTooLarge or IssueCode.DuplicateDocument);
        }
    }
}
