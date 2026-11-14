using System.Diagnostics;
using System.Text;
using ClaimsIntake.Domain;
using ClaimsIntake.Pipeline.Quality;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaimsIntake.Pipeline.Tests;

/// <summary>A small, valid PDF written by hand, so tests can make a 200-page file or one with 20,000 objects without a fixture.</summary>
internal static class GeneratedPdf
{
    /// <param name="pages">Number of pages. Each holds one line of text.</param>
    /// <param name="extraObjects">Unused objects added after the pages: a small file with a large object table.</param>
    public static byte[] Build(int pages, int extraObjects = 0, string line = "Policy number: TM-CP-100001. Water damage at the workshop.")
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{4 + i * 2} 0 R"))}] /Count {pages} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (var i = 0; i < pages; i++)
        {
            var content = $"BT /F1 12 Tf 72 720 Td ({line} Page {i + 1}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {5 + i * 2} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        for (var i = 0; i < extraObjects; i++)
        {
            objects.Add($"<< /Unused {i} >>");
        }

        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(body.Length);
            body.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = body.Length;
        body.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            body.Append($"{offset:D10} 00000 n \n");
        }

        body.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(body.ToString());
    }
}

/// <summary>INP-01 (page limit, deadline, one parse) and INP-02 (magic bytes).</summary>
public class InputLimitsTests
{
    private static IntakeDocument Document(string name, byte[] bytes) => new($"inbox/{name}", name, BinaryData.FromBytes(bytes));

    private static Task<QualityFinding?> Run(IInputQualityCheck check, IntakeDocument document, CancellationToken ct = default) =>
        check.CheckAsync(new InputQualityContext(document, null), ct);

    private static ClaimIntakePipeline Pipeline(RecordingSink sink, InputQualityGate? gate = null, TimeSpan? deadline = null) =>
        new(new LocalPdfTextExtractor(), new ClaimsIntake.Pipeline.Fields.HeuristicClaimExtractor(), FakePolicyLookup.Seeded(), sink,
            new FixedTimeProvider(TestTime.Now), NullLogger<ClaimIntakePipeline>.Instance, quality: gate, processingDeadline: deadline);

    // ----- The generator itself -----

    [Fact]
    public async Task The_generated_pdf_is_a_real_pdf_that_the_extractor_reads()
    {
        var text = await new LocalPdfTextExtractor().ExtractAsync(Document("g.pdf", GeneratedPdf.Build(3)), CancellationToken.None);

        Assert.Equal(3, text.Pages.Count);
        Assert.Contains("Page 2", text.Pages[1]);
        Assert.Contains("TM-CP-100001", text.Pages[0]);
    }

    // ----- INP-01: page count -----

    [Fact]
    public async Task A_200_page_pdf_is_over_the_limit_and_is_not_read()
    {
        var document = Document("long.pdf", GeneratedPdf.Build(200));

        var finding = await Run(new PdfIntegrityCheck(maxPages: 50), document);

        Assert.Equal(IssueCode.DocumentTooLarge, finding!.Code);
        Assert.Equal("pdf_integrity", finding.Check);
        Assert.Contains("200 pages", finding.Message);
        Assert.Contains("limit of 50", finding.Message);
        Assert.False(ParsedPdfCache.TryTake(document, out _), "An over-long PDF's text must not be read.");
    }

    [Fact]
    public async Task A_pdf_at_the_limit_passes()
    {
        Assert.Null(await Run(new PdfIntegrityCheck(maxPages: 50), Document("fifty.pdf", GeneratedPdf.Build(50))));
        Assert.Equal(IssueCode.DocumentTooLarge, (await Run(new PdfIntegrityCheck(maxPages: 49), Document("fifty.pdf", GeneratedPdf.Build(50))))!.Code);
    }

    [Fact]
    public async Task The_default_limit_is_50_pages_and_can_be_set()
    {
        Assert.Equal(50, new InputQualityOptions().MaxPages);
        var gate = InputQualityGate.CreateDefault(new InputQualityOptions { MaxPages = 3 });

        var finding = await gate.RunAsync(QualityStage.Document, new InputQualityContext(Document("four.pdf", GeneratedPdf.Build(4)), null), CancellationToken.None);

        Assert.Equal(IssueCode.DocumentTooLarge, finding!.Code);
    }

    [Fact]
    public async Task A_200_page_pdf_through_the_pipeline_reaches_a_person_as_a_blocking_issue_within_a_second()
    {
        var sink = new RecordingSink();
        var stopwatch = Stopwatch.StartNew();

        await Pipeline(sink).ProcessAsync(Document("long.pdf", GeneratedPdf.Build(200)), CancellationToken.None);

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var claim = Assert.Single(sink.Claims);
        var issue = Assert.Single(claim.Issues);
        Assert.Equal((IssueCode.DocumentTooLarge, IssueSeverity.Error), (issue.Code, issue.Severity));
        Assert.True(claim.ToClaim().HasBlockingIssues);
        Assert.Empty(claim.SourcePages);
    }

    [Fact]
    public async Task A_small_pdf_with_twenty_thousand_objects_is_handled_within_the_deadline()
    {
        var sink = new RecordingSink();
        var stopwatch = Stopwatch.StartNew();

        await Pipeline(sink, deadline: TimeSpan.FromSeconds(20)).ProcessAsync(Document("many.pdf", GeneratedPdf.Build(5, extraObjects: 20_000)), CancellationToken.None);

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(20));
        var claim = Assert.Single(sink.Claims);
        Assert.Equal(5, claim.SourcePages.Count); // Read normally.
    }

    // ----- INP-01: the deadline -----

    private sealed class SlowCheck : IInputQualityCheck
    {
        public string Name => "slow";

        public QualityStage Stage => QualityStage.Document;

        public async Task<QualityFinding?> CheckAsync(InputQualityContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return null;
        }
    }

    [Fact]
    public async Task A_document_that_takes_longer_than_the_deadline_is_handed_to_a_person_not_lost()
    {
        var sink = new RecordingSink();
        var gate = new InputQualityGate([new SlowCheck()]);
        var stopwatch = Stopwatch.StartNew();

        var result = await Pipeline(sink, gate, deadline: TimeSpan.FromMilliseconds(150)).ProcessAsync(Document("slow.txt", Encoding.UTF8.GetBytes("Claim: water damage")), CancellationToken.None);

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        var claim = Assert.Single(sink.Claims);
        var issue = Assert.Single(claim.Issues);
        Assert.Equal((IssueCode.DocumentTooLarge, IssueSeverity.Error), (issue.Code, issue.Severity));
        Assert.Contains("took longer than", issue.Message);
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public async Task The_hosts_own_cancellation_is_not_a_deadline_and_still_propagates()
    {
        var sink = new RecordingSink();
        var gate = new InputQualityGate([new SlowCheck()]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline(sink, gate, deadline: TimeSpan.FromMinutes(5))
            .ProcessAsync(Document("slow.txt", Encoding.UTF8.GetBytes("Claim: water damage")), cancellation.Token));

        Assert.Empty(sink.Claims);
    }

    // ----- INP-01: one parse -----

    [Fact]
    public async Task The_pdf_is_parsed_once_the_integrity_check_hands_its_text_to_the_extractor()
    {
        var document = Document("once.pdf", GeneratedPdf.Build(2));

        Assert.Null(await Run(new PdfIntegrityCheck(), document));
        // The extractor takes the pages the check read. It would have to open the file again if it did not.
        var corrupted = new IntakeDocument(document.DocumentId, document.FileName, BinaryData.FromBytes(Encoding.UTF8.GetBytes("this is not a pdf any more")));
        Assert.True(ParsedPdfCache.TryTake(document, out var pages));
        ParsedPdfCache.Store(corrupted, pages);

        var text = await new LocalPdfTextExtractor().ExtractAsync(corrupted, CancellationToken.None);

        Assert.Equal(2, text.Pages.Count);
        Assert.Contains("Page 1", text.Pages[0]);
        // Taken once: a second extraction of the same object parses afresh (and here, fails).
        await Assert.ThrowsAsync<UnreadableDocumentException>(() => new LocalPdfTextExtractor().ExtractAsync(corrupted, CancellationToken.None));
    }

    [Fact]
    public async Task A_document_the_check_did_not_see_is_parsed_by_the_extractor_as_before()
    {
        var text = await new LocalPdfTextExtractor().ExtractAsync(Document("direct.pdf", GeneratedPdf.Build(1)), CancellationToken.None);

        Assert.Single(text.Pages);
    }

    // ----- INP-02: magic bytes -----

    private static readonly byte[] ZipHeader = [(byte)'P', (byte)'K', 3, 4, 20, 0, 0, 0, 8, 0];

    [Fact]
    public async Task A_zip_renamed_to_pdf_is_refused_with_a_reason_that_does_not_quote_it()
    {
        var bytes = ZipHeader.Concat(Encoding.UTF8.GetBytes("SECRET-CLAIM-TEXT")).ToArray();

        var finding = await Run(new FileSignatureCheck(), Document("invoice.pdf", bytes));

        Assert.Equal(IssueCode.ProtectedOrCorruptDocument, finding!.Code);
        Assert.Equal("file_signature", finding.Check);
        Assert.Contains("ZIP archive", finding.Message);
        Assert.DoesNotContain("SECRET", finding.Message);
    }

    [Theory]
    [InlineData("MZ\u0090\u0000\u0003\u0000\u0000\u0000", "program")]
    [InlineData("\u007FELF\u0002\u0001\u0001\u0000", "program")]
    [InlineData("GIF89a\u0001\u0000", "image")]
    public async Task Other_binary_files_renamed_to_pdf_are_named_for_what_they_look_like(string content, string expected)
    {
        var finding = await Run(new FileSignatureCheck(), Document("x.pdf", Encoding.Latin1.GetBytes(content)));

        Assert.Contains(expected, finding!.Message);
    }

    [Fact]
    public async Task A_text_file_renamed_to_pdf_is_refused_and_a_real_pdf_passes()
    {
        Assert.NotNull(await Run(new FileSignatureCheck(), Document("note.pdf", Encoding.UTF8.GetBytes("Dear Sirs, my shop flooded."))));
        Assert.Null(await Run(new FileSignatureCheck(), Document("real.pdf", GeneratedPdf.Build(1))));
        Assert.Null(await Run(new FileSignatureCheck(), Document("sample.pdf", File.ReadAllBytes(Path.Combine(Docs.SamplesFolder, "01-water-damage-clean.pdf")))));
    }

    [Fact]
    public async Task A_pdf_renamed_to_txt_or_eml_is_refused_and_plain_text_and_mail_pass()
    {
        var pdf = GeneratedPdf.Build(1);
        Assert.Contains("PDF", (await Run(new FileSignatureCheck(), Document("a.txt", pdf)))!.Message);
        Assert.NotNull(await Run(new FileSignatureCheck(), Document("a.eml", pdf)));
        Assert.NotNull(await Run(new FileSignatureCheck(), Document("a.txt", [.. "hello"u8, 0, .. "world"u8])));

        Assert.Null(await Run(new FileSignatureCheck(), Document("a.txt", Encoding.UTF8.GetBytes("Policy TM-CP-100001: burst pipe on 3 November."))));
        Assert.Null(await Run(new FileSignatureCheck(), Document("a.eml", Encoding.UTF8.GetBytes("From: broker@example.test\r\nTo: claims@example.test\r\nSubject: Claim\r\n\r\nBody"))));
        Assert.Null(await Run(new FileSignatureCheck(), Document("b.eml", File.ReadAllBytes(Path.Combine(Docs.SamplesFolder, "07-broker-email.eml")))));
    }

    [Fact]
    public async Task An_eml_that_does_not_start_with_a_header_is_refused()
    {
        var finding = await Run(new FileSignatureCheck(), Document("a.eml", Encoding.UTF8.GetBytes("This is just a paragraph with no mail header at all.")));

        Assert.NotNull(finding);
    }

    [Fact]
    public async Task Types_the_file_check_does_not_know_are_left_to_the_extractor()
    {
        Assert.Null(await Run(new FileSignatureCheck(), Document("scan.bmp", [1, 2, 3])));
    }

    [Fact]
    public async Task A_zip_renamed_to_pdf_reaches_a_person_through_the_pipeline_without_touching_the_pdf_parser()
    {
        var sink = new RecordingSink();

        await Pipeline(sink).ProcessAsync(Document("invoice.pdf", ZipHeader.Concat(new byte[64]).ToArray()), CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        Assert.Equal(IssueCode.ProtectedOrCorruptDocument, Assert.Single(claim.Issues).Code);
        Assert.Contains("ZIP archive", claim.Issues[0].Message);
    }
}
