using ClaimsIntake.Pipeline.Handoff;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ClaimsIntake.Pipeline.Tests;

public sealed class LocalInboxWatcherTests : IDisposable
{
    private readonly string _inbox = Path.Combine(Path.GetTempPath(), $"claims-inbox-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_inbox))
        {
            Directory.Delete(_inbox, recursive: true);
        }
    }

    private LocalInboxWatcher Watcher(RecordingSink sink, Legacy.IPolicyLookup? policies = null, int maxAttempts = 3) =>
        new(Docs.Pipeline(policies, sink),
            Options.Create(new LocalInboxOptions { Path = _inbox, PollInterval = TimeSpan.FromMilliseconds(50), MaxAttempts = maxAttempts }),
            new ListLogger<LocalInboxWatcher>());

    private string Drop(string name, byte[] content)
    {
        Directory.CreateDirectory(_inbox);
        var path = Path.Combine(_inbox, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string DropSample(string name) => Drop(name, File.ReadAllBytes(Path.Combine(Docs.SamplesFolder, name)));

    private string[] Names(string folder) =>
        Directory.Exists(Path.Combine(_inbox, folder))
            ? [.. Directory.EnumerateFiles(Path.Combine(_inbox, folder)).Select(Path.GetFileName).Order()!]
            : [];

    [Fact]
    public async Task A_dropped_document_is_processed_handed_off_and_moved_to_processed()
    {
        var sink = new RecordingSink();
        DropSample("01-water-damage-clean.pdf");
        DropSample("06-email-fire.txt");

        var handedOff = await Watcher(sink).ScanOnceAsync(CancellationToken.None);

        Assert.Equal(2, handedOff);
        Assert.Equal(["inbox/01-water-damage-clean.pdf", "inbox/06-email-fire.txt"], sink.Claims.Select(c => c.DocumentId).Order());
        Assert.Equal(["01-water-damage-clean.pdf", "06-email-fire.txt"], Names(LocalInboxWatcher.ProcessedFolder));
        Assert.Empty(Directory.EnumerateFiles(_inbox));
    }

    [Fact]
    public async Task An_empty_inbox_is_fine_and_is_created_on_demand()
    {
        var handedOff = await Watcher(new RecordingSink()).ScanOnceAsync(CancellationToken.None);

        Assert.Equal(0, handedOff);
        Assert.True(Directory.Exists(_inbox));
    }

    [Fact]
    public async Task An_unreadable_pdf_is_moved_to_failed_and_does_not_stop_the_others()
    {
        var sink = new RecordingSink();
        Drop("00-broken.pdf", "not a pdf"u8.ToArray());
        DropSample("02-theft-clean.pdf");

        var handedOff = await Watcher(sink).ScanOnceAsync(CancellationToken.None);

        Assert.Equal(1, handedOff);
        Assert.Equal(["00-broken.pdf"], Names(LocalInboxWatcher.FailedFolder));
        Assert.Equal(["02-theft-clean.pdf"], Names(LocalInboxWatcher.ProcessedFolder));
    }

    [Fact]
    public async Task An_unsupported_file_type_is_moved_to_failed()
    {
        Drop("notes.docx", "x"u8.ToArray());

        await Watcher(new RecordingSink()).ScanOnceAsync(CancellationToken.None);

        Assert.Equal(["notes.docx"], Names(LocalInboxWatcher.FailedFolder));
    }

    [Fact]
    public async Task Hidden_and_temporary_files_are_left_alone()
    {
        var sink = new RecordingSink();
        Drop(".DS_Store", [1]);
        Drop("~$draft.txt", [1]);

        await Watcher(sink).ScanOnceAsync(CancellationToken.None);

        Assert.Empty(sink.Claims);
        Assert.Equal(2, Directory.EnumerateFiles(_inbox).Count());
    }

    [Fact]
    public async Task A_file_still_being_written_is_skipped_until_it_is_released()
    {
        var sink = new RecordingSink();
        var path = DropSample("01-water-damage-clean.pdf");
        var watcher = Watcher(sink);

        await using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, await watcher.ScanOnceAsync(CancellationToken.None));
            Assert.Empty(sink.Claims);
        }

        Assert.Equal(1, await watcher.ScanOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task When_the_legacy_system_is_down_the_file_stays_and_is_retried_then_fails_after_the_limit()
    {
        var sink = new RecordingSink();
        DropSample("01-water-damage-clean.pdf");
        var watcher = Watcher(sink, new DownPolicyLookup(), maxAttempts: 3);

        await watcher.ScanOnceAsync(CancellationToken.None);
        await watcher.ScanOnceAsync(CancellationToken.None);
        Assert.Single(Directory.EnumerateFiles(_inbox)); // Still waiting after two failures.

        await watcher.ScanOnceAsync(CancellationToken.None);

        Assert.Equal(["01-water-damage-clean.pdf"], Names(LocalInboxWatcher.FailedFolder));
        Assert.Empty(sink.Claims);
    }

    [Fact]
    public async Task A_hand_off_failure_leaves_the_file_for_the_next_scan()
    {
        var sink = new RecordingSink { FailWith = new HandoffFailedException("api down") };
        DropSample("01-water-damage-clean.pdf");
        var watcher = Watcher(sink);

        Assert.Equal(0, await watcher.ScanOnceAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(_inbox));

        sink.FailWith = null;

        Assert.Equal(1, await watcher.ScanOnceAsync(CancellationToken.None));
        Assert.Equal(["01-water-damage-clean.pdf"], Names(LocalInboxWatcher.ProcessedFolder));
    }

    [Fact]
    public async Task The_same_file_name_dropped_twice_is_processed_twice_without_overwriting_the_first()
    {
        var sink = new RecordingSink();
        var watcher = Watcher(sink);

        DropSample("01-water-damage-clean.pdf");
        await watcher.ScanOnceAsync(CancellationToken.None);
        DropSample("01-water-damage-clean.pdf");
        await watcher.ScanOnceAsync(CancellationToken.None);

        Assert.Equal(2, Names(LocalInboxWatcher.ProcessedFolder).Length);
        Assert.Equal(sink.Claims[0].ClaimId, sink.Claims[1].ClaimId); // The API recognises the repeat.
    }

    [Fact]
    public async Task As_a_background_service_it_picks_up_a_file_dropped_while_it_runs_and_stops_cleanly()
    {
        var sink = new RecordingSink();
        IHostedService service = Watcher(sink);

        await service.StartAsync(CancellationToken.None);
        try
        {
            DropSample("02-theft-clean.pdf");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (sink.Claims.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Single(sink.Claims);
        Assert.Equal(["02-theft-clean.pdf"], Names(LocalInboxWatcher.ProcessedFolder));
    }
}
