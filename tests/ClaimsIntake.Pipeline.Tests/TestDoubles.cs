using System.Text;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Pipeline;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.Logging;

namespace ClaimsIntake.Pipeline.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class TestTime
{
    /// <summary>The "today" the sample documents were written against.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
}

/// <summary>The ten policies from the legacy seed data that the samples use, with their periods.</summary>
internal sealed class FakePolicyLookup(params PolicyReference[] policies) : IPolicyLookup
{
    public List<string> Lookups { get; } = [];

    public static FakePolicyLookup Seeded() => new(
        new PolicyReference("TM-CP-100001", "Brindle & Marsh Joinery Ltd", new(2026, 1, 1), new(2026, 12, 31)),
        new PolicyReference("TM-CP-100002", "Kestrel Print & Packaging Ltd", new(2026, 3, 1), new(2027, 2, 28)),
        new PolicyReference("TM-CP-100003", "Harcourt Dental Practice", new(2026, 6, 15), new(2027, 6, 14)),
        new PolicyReference("TM-CP-100004", "Lowther Fabrication Works Ltd", new(2025, 11, 1), new(2026, 10, 31)),
        new PolicyReference("TM-CP-100005", "Pemberton Road Haulage Ltd", new(2026, 4, 1), new(2027, 3, 31)),
        new PolicyReference("TM-CP-100006", "Wren & Co Estate Agents", new(2026, 2, 1), new(2027, 1, 31)),
        new PolicyReference("TM-CP-100007", "Tideway Catering Supplies Ltd", new(2026, 9, 1), new(2027, 8, 31)));

    public Task<PolicyReference?> FindAsync(string policyNumber, CancellationToken cancellationToken)
    {
        Lookups.Add(policyNumber);
        return Task.FromResult(policies.FirstOrDefault(p => p.Number.Equals(policyNumber, StringComparison.OrdinalIgnoreCase)));
    }
}

internal sealed class DownPolicyLookup : IPolicyLookup
{
    public Task<PolicyReference?> FindAsync(string policyNumber, CancellationToken cancellationToken) =>
        throw new LegacySystemUnavailableException("down");
}

internal sealed class RecordingSink : IClaimSink
{
    public List<ProposedClaim> Claims { get; } = [];
    public Exception? FailWith { get; set; }

    public Task HandOffAsync(ProposedClaim claim, CancellationToken cancellationToken)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Claims.Add(claim);
        return Task.CompletedTask;
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

/// <summary>Captures every log line, formatted, so tests can check that no document text reaches the logs.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception));
}

internal static class Docs
{
    public static IntakeDocument Text(string name, string text) =>
        new($"inbox/{name}", name, BinaryData.FromBytes(Encoding.UTF8.GetBytes(text)));

    public static string SamplesFolder => Path.Combine(AppContext.BaseDirectory, "samples");

    public static IntakeDocument Sample(string fileName) =>
        new($"inbox/{fileName}", fileName, BinaryData.FromBytes(File.ReadAllBytes(Path.Combine(SamplesFolder, fileName))));

    public static ClaimIntakePipeline Pipeline(
        IPolicyLookup? policies = null,
        IClaimSink? sink = null,
        IClaimExtractor? extractor = null,
        ILogger<ClaimIntakePipeline>? logger = null) =>
        new(new LocalPdfTextExtractor(),
            extractor ?? new HeuristicClaimExtractor(),
            policies ?? FakePolicyLookup.Seeded(),
            sink ?? new RecordingSink(),
            new FixedTimeProvider(TestTime.Now),
            logger ?? new ListLogger<ClaimIntakePipeline>());
}
