using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Pipeline.Assessment;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaimsIntake.Pipeline.Tests;

/// <summary>
/// The Milestone 3 defect: the notification period was measured to the wall clock, so a document that was "on time" when
/// it was written became "late" as the days went by. The date now comes from the document, or from when it was received.
/// </summary>
public class NotificationDateTests
{
    private static DocumentText Page(string text) => new([text]);

    [Theory]
    [InlineData("Date notified: 6 November 2026", "2026-11-06")]
    [InlineData("Date reported: 06/11/2026", "2026-11-06")]
    [InlineData("Notified on 3rd Nov 2026", "2026-11-03")]
    [InlineData("Notification date: 2026-11-09", "2026-11-09")]
    [InlineData("Date received: Friday 13 November 2026", "2026-11-13")]
    public void A_labelled_notification_date_is_read(string line, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), NotificationDateReader.Read(Page($"Policy number: TM-CP-100001\n{line}\nDate of loss: 1 November 2026")));

    [Theory]
    [InlineData("Date: Tue, 10 Nov 2026 08:14:02 +0000", "2026-11-10")]
    [InlineData("Sent: 12 November 2026 16:01", "2026-11-12")]
    public void The_sent_date_of_an_email_is_the_notification_date(string header, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), NotificationDateReader.Read(Page($"From: a@b.example\n{header}\nSubject: Claim\n\nHello")));

    [Fact]
    public void A_labelled_date_wins_over_the_email_header()
    {
        var text = Page("Date: Tue, 10 Nov 2026 08:14:02 +0000\nDate notified: 9 November 2026");

        Assert.Equal(new DateOnly(2026, 11, 9), NotificationDateReader.Read(text));
    }

    [Fact]
    public void The_date_of_loss_is_never_mistaken_for_the_notification_date() =>
        Assert.Null(NotificationDateReader.Read(Page("Date of loss: 3 November 2026\nIncident date: 3 November 2026")));

    [Fact]
    public void A_document_that_states_no_date_gives_null_so_the_caller_uses_the_received_time() =>
        Assert.Null(NotificationDateReader.Read(Page("Policy number: TM-CP-100001\nA pipe burst.")));

    [Fact]
    public void A_notification_date_that_cannot_be_read_is_ignored() =>
        Assert.Null(NotificationDateReader.Read(Page("Date notified: 31/02/2026")));

    [Fact]
    public async Task The_heuristic_extractor_does_not_take_the_notification_date_as_the_loss_date()
    {
        var fields = await new HeuristicClaimExtractor().ExtractAsync("d", Page("Policy number: TM-CP-100001\nDate notified: 14 November 2026\nA pipe burst."), CancellationToken.None);

        Assert.Null(fields.Single(f => f.Name == ClaimFields.LossDate).Value);
    }

    private static ClaimIntakePipeline PipelineAt(DateTimeOffset clock, RecordingSink sink)
    {
        var runner = Assessing.Runner(new RuleBasedClaimAssessor());
        return new ClaimIntakePipeline(new LocalPdfTextExtractor(), new HeuristicClaimExtractor(), FakePolicyLookup.Seeded(), sink,
            new FixedTimeProvider(clock), NullLogger<ClaimIntakePipeline>.Instance, runner);
    }

    private static string[] Kinds(ProposedClaim claim) =>
        [.. claim.Assessment!.Findings.Select(f => $"{f.Kind}:{string.Join(',', f.Citations.Select(c => c.ClauseRef))}")];

    [Theory]
    [InlineData("2026-11-14T09:00:00Z")]
    [InlineData("2027-06-01T09:00:00Z")]
    [InlineData("2030-01-01T00:00:00Z")]
    public async Task A_clean_document_is_never_late_whatever_the_day_it_is_processed(string clock)
    {
        var sink = new RecordingSink();

        await PipelineAt(DateTimeOffset.Parse(clock), sink).ProcessAsync(Docs.Sample("01-water-damage-clean.pdf"), CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        Assert.DoesNotContain(claim.Assessment!.Findings, f => f.Kind == FindingKind.NotificationBreach);
        Assert.Equal(ReviewRoute.StraightToHandler, claim.Assessment.Route);
        Assert.Equal(new DateOnly(2026, 11, 6), claim.NotifiedOn);
        Assert.Equal(NotificationDateSources.Document, claim.NotifiedOnSource);
        Assert.Empty(claim.Issues);
    }

    [Theory]
    [InlineData("2026-11-14T09:00:00Z")]
    [InlineData("2031-03-09T12:00:00Z")]
    public async Task A_late_document_is_late_on_every_day_and_for_the_same_reason(string clock)
    {
        var sink = new RecordingSink();

        await PipelineAt(DateTimeOffset.Parse(clock), sink).ProcessAsync(Docs.Sample("10-unoccupied-late-notification.pdf"), CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        Assert.Equal(["PossibleExclusion:4.3", "NotificationBreach:5.1"], Kinds(claim).Where(k => !k.StartsWith("CoverageClause", StringComparison.Ordinal)));
        Assert.Contains("40 days", claim.Assessment!.Findings.Single(f => f.Kind == FindingKind.NotificationBreach).Summary);
    }

    [Fact]
    public async Task An_email_uses_its_sent_date_not_the_day_it_is_processed()
    {
        var sink = new RecordingSink();

        // 06-email-fire.txt: loss on 9 November, sent on 10 November, on a policy with a 14-day notification period.
        await PipelineAt(DateTimeOffset.Parse("2027-01-01T00:00:00Z"), sink).ProcessAsync(Docs.Sample("06-email-fire.txt"), CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        Assert.Equal(new DateOnly(2026, 11, 10), claim.NotifiedOn);
        Assert.Equal(NotificationDateSources.Document, claim.NotifiedOnSource);
        Assert.DoesNotContain(claim.Assessment!.Findings, f => f.Kind == FindingKind.NotificationBreach);
    }

    [Fact]
    public async Task A_document_with_no_stated_date_uses_the_time_it_was_received_not_the_pipeline_clock()
    {
        var sink = new RecordingSink();
        var received = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var document = Docs.Sample("08-garbled-layout.pdf") with { ReceivedAt = received };

        await PipelineAt(DateTimeOffset.Parse("2029-05-05T00:00:00Z"), sink).ProcessAsync(document, CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        Assert.Equal(new DateOnly(2026, 11, 12), claim.NotifiedOn);
        Assert.Equal(NotificationDateSources.Received, claim.NotifiedOnSource);
        Assert.Equal(received, claim.ReceivedAt);
        Assert.DoesNotContain(claim.Assessment!.Findings, f => f.Kind == FindingKind.NotificationBreach);
    }

    [Fact]
    public async Task A_received_time_after_midnight_UTC_is_a_UK_date()
    {
        var sink = new RecordingSink();
        // 23:30 UTC on 30 June is 00:30 on 1 July in London (BST).
        var received = new DateTimeOffset(2026, 6, 30, 23, 30, 0, TimeSpan.Zero);

        await PipelineAt(DateTimeOffset.UtcNow, sink).ProcessAsync(Docs.Sample("08-garbled-layout.pdf") with { ReceivedAt = received }, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 7, 1), Assert.Single(sink.Claims).NotifiedOn);
    }

    [Fact]
    public async Task A_stated_date_beats_a_later_received_time()
    {
        var sink = new RecordingSink();
        // The email was sent on 10 November; the file only reached us on 1 January. The loss was on 9 November.
        var document = Docs.Sample("06-email-fire.txt") with { ReceivedAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        await PipelineAt(TestTime.Now, sink).ProcessAsync(document, CancellationToken.None);

        Assert.DoesNotContain(Assert.Single(sink.Claims).Assessment!.Findings, f => f.Kind == FindingKind.NotificationBreach);
    }

    [Fact]
    public async Task A_loss_date_after_the_notification_date_is_a_future_loss_whatever_the_clock_says()
    {
        var sink = new RecordingSink();

        // 04-future-loss-date.pdf: loss on 15 February 2027, notified on 14 November 2026. A clock in 2030 must not hide it.
        await PipelineAt(DateTimeOffset.Parse("2030-01-01T00:00:00Z"), sink).ProcessAsync(Docs.Sample("04-future-loss-date.pdf"), CancellationToken.None);

        Assert.Contains(Assert.Single(sink.Claims).Issues, i => i.Code == IssueCode.LossDateInFuture);
    }

    [Fact]
    public async Task Without_a_received_time_the_pipeline_clock_is_used()
    {
        var sink = new RecordingSink();

        await PipelineAt(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero), sink).ProcessAsync(Docs.Sample("08-garbled-layout.pdf"), CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 11, 12), Assert.Single(sink.Claims).NotifiedOn);
    }
}
