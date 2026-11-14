using System.Text;
using Azure;
using ClaimsIntake.Pipeline.Text;

namespace ClaimsIntake.Pipeline.Tests;

public class LocalPdfTextExtractorTests
{
    private readonly LocalPdfTextExtractor _extractor = new();

    [Fact]
    public async Task A_text_based_pdf_gives_its_text_in_reading_order()
    {
        var text = await _extractor.ExtractAsync(Docs.Sample("01-water-damage-clean.pdf"), CancellationToken.None);

        var page = Assert.Single(text.Pages);
        Assert.Contains("Policy number: TM-CP-100001", page);
        Assert.Contains("Insured: Brindle & Marsh Joinery Ltd", page);
        Assert.True(page.IndexOf("Policy number", StringComparison.Ordinal) < page.IndexOf("Date of loss", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("note.txt")]
    [InlineData("mail.eml")]
    [InlineData("SHOUTING.TXT")]
    public async Task Text_and_email_files_are_read_as_one_page_with_unix_line_endings(string name)
    {
        var text = await _extractor.ExtractAsync(Docs.Text(name, "Line one\r\nLine two\r\n"), CancellationToken.None);

        Assert.Equal(["Line one\nLine two"], text.Pages);
    }

    [Theory]
    [InlineData("claim.docx")]
    [InlineData("claim.png")]
    [InlineData("claim")]
    public async Task Other_file_types_are_refused(string name)
    {
        var ex = await Assert.ThrowsAsync<UnreadableDocumentException>(
            () => _extractor.ExtractAsync(Docs.Text(name, "hello"), CancellationToken.None));

        Assert.Contains(".pdf", ex.Message);
    }

    [Fact]
    public async Task Bytes_that_are_not_a_pdf_are_refused_without_quoting_them()
    {
        var document = new IntakeDocument("inbox/bad.pdf", "bad.pdf", BinaryData.FromBytes(Encoding.UTF8.GetBytes("this is not a pdf, it says SECRET-CLAIM-TEXT")));

        var ex = await Assert.ThrowsAsync<UnreadableDocumentException>(() => _extractor.ExtractAsync(document, CancellationToken.None));

        Assert.DoesNotContain("SECRET-CLAIM-TEXT", ex.Message);
    }

    [Fact]
    public async Task A_file_with_no_text_is_refused_with_a_hint_about_scans()
    {
        var ex = await Assert.ThrowsAsync<UnreadableDocumentException>(
            () => _extractor.ExtractAsync(Docs.Text("blank.txt", "  \r\n "), CancellationToken.None));

        Assert.Contains("scan", ex.Message);
    }
}

public class AzureDocumentIntelligenceExtractorTests
{
    private sealed class FakeAnalyzer(Func<BinaryData, IReadOnlyList<string>> analyse) : IDocumentLayoutAnalyzer
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> AnalyzePagesAsync(BinaryData content, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(analyse(content));
        }
    }

    [Fact]
    public async Task The_pages_from_layout_analysis_become_the_document_text()
    {
        var analyzer = new FakeAnalyzer(_ => ["Page one text", "Page two text"]);
        var extractor = new AzureDocumentIntelligenceExtractor(analyzer);

        var text = await extractor.ExtractAsync(Docs.Text("scan.pdf", "bytes"), CancellationToken.None);

        Assert.Equal(["Page one text", "Page two text"], text.Pages);
        Assert.Equal(1, analyzer.Calls);
    }

    [Fact]
    public async Task A_document_with_no_recognised_text_is_refused()
    {
        var extractor = new AzureDocumentIntelligenceExtractor(new FakeAnalyzer(_ => ["", "  "]));

        await Assert.ThrowsAsync<UnreadableDocumentException>(() => extractor.ExtractAsync(Docs.Text("scan.pdf", "bytes"), CancellationToken.None));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(415)]
    public async Task A_bad_request_from_the_service_means_the_document_is_unreadable(int status)
    {
        var analyzer = new ThrowingAnalyzer(new RequestFailedException(status, "Invalid content: SECRET-CLAIM-TEXT"));
        var extractor = new AzureDocumentIntelligenceExtractor(analyzer);

        var ex = await Assert.ThrowsAsync<UnreadableDocumentException>(() => extractor.ExtractAsync(Docs.Text("scan.pdf", "bytes"), CancellationToken.None));

        Assert.DoesNotContain("SECRET-CLAIM-TEXT", ex.Message);
    }

    [Fact]
    public async Task A_service_outage_is_not_mistaken_for_a_bad_document()
    {
        var extractor = new AzureDocumentIntelligenceExtractor(new ThrowingAnalyzer(new RequestFailedException(503, "unavailable")));

        await Assert.ThrowsAsync<RequestFailedException>(() => extractor.ExtractAsync(Docs.Text("scan.pdf", "bytes"), CancellationToken.None));
    }

    private sealed class ThrowingAnalyzer(Exception ex) : IDocumentLayoutAnalyzer
    {
        public Task<IReadOnlyList<string>> AnalyzePagesAsync(BinaryData content, CancellationToken cancellationToken) => throw ex;
    }
}
