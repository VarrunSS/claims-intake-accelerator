using System.Net;
using System.Net.Http.Json;
using System.Text;
using Azure.Messaging.ServiceBus;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;

namespace ClaimsIntake.Pipeline.Tests;

public class HandoffAndLookupTests
{
    private static ProposedClaim Proposed(string documentId = "inbox/a.pdf") => new(
        documentId,
        TestTime.Now,
        [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.97, new SourceSpan(documentId, 1, 4, 12))],
        [new ValidationIssue(IssueCode.LowConfidence, IssueSeverity.Warning, ClaimFields.LossDate, "Low confidence.")],
        ["Policy TM-CP-100001"]);

    private sealed class FakeSender(Exception? failWith = null) : ServiceBusSender
    {
        public List<ServiceBusMessage> Sent { get; } = [];

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            if (failWith is not null)
            {
                throw failWith;
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    // ----- Service Bus sink -----

    [Fact]
    public async Task The_service_bus_sink_sends_json_with_the_claim_id_as_message_id()
    {
        var sender = new FakeSender();
        var claim = Proposed();

        await new ServiceBusClaimSink(sender).HandOffAsync(claim, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.Equal(claim.ClaimId.ToString("D"), message.MessageId);
        Assert.Equal("application/json", message.ContentType);
        Assert.Equal(ServiceBusClaimSink.Subject, message.Subject);
        var back = ProposedClaim.FromJson(message.Body.ToString());
        Assert.Equal(claim.DocumentId, back.DocumentId);
        Assert.Equal("TM-CP-100001", back.Fields.Single().Value);
        Assert.Equal(IssueCode.LowConfidence, back.Issues.Single().Code);
        Assert.Contains("\"LowConfidence\"", message.Body.ToString()); // Enums are written as names.
    }

    [Fact]
    public async Task A_service_bus_failure_becomes_a_hand_off_failure()
    {
        var sender = new FakeSender(new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy));

        var ex = await Assert.ThrowsAsync<HandoffFailedException>(
            () => new ServiceBusClaimSink(sender).HandOffAsync(Proposed(), CancellationToken.None));

        Assert.Contains("ServiceBusy", ex.Message);
    }

    // ----- HTTP sink -----

    [Fact]
    public async Task The_http_sink_posts_the_claim_to_the_intake_endpoint()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var sink = new HttpClaimSink(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") });

        await sink.HandOffAsync(Proposed(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://api.test/intake/claims", request.RequestUri!.ToString());
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Equal("inbox/a.pdf", ProposedClaim.FromJson(body).DocumentId);
    }

    [Fact]
    public async Task The_http_sink_treats_a_duplicate_answer_as_success()
    {
        var sink = new HttpClaimSink(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))) { BaseAddress = new Uri("http://api.test/") });

        await sink.HandOffAsync(Proposed(), CancellationToken.None);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task The_http_sink_reports_a_refusal_with_the_status_only(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("echo: Brindle") });
        var sink = new HttpClaimSink(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") });

        var ex = await Assert.ThrowsAsync<HandoffFailedException>(() => sink.HandOffAsync(Proposed(), CancellationToken.None));

        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.DoesNotContain("Brindle", ex.Message);
    }

    [Fact]
    public async Task The_http_sink_reports_an_unreachable_api()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var sink = new HttpClaimSink(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") });

        await Assert.ThrowsAsync<HandoffFailedException>(() => sink.HandOffAsync(Proposed(), CancellationToken.None));
    }

    // ----- Policy lookup -----

    private static LegacyPolicyLookup Lookup(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://legacy.test/") });

    [Fact]
    public async Task A_known_policy_is_read_from_the_legacy_system()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"number":"TM-CP-100001","insuredName":"Brindle & Marsh Joinery Ltd","product":"Commercial Combined","periodStart":"2026-01-01","periodEnd":"2026-12-31","sumInsuredGbp":750000,"excessGbp":500}""",
                Encoding.UTF8, "application/json"),
        });

        var policy = await Lookup(handler).FindAsync(" tm-cp-100001 ", CancellationToken.None);

        Assert.Equal(new PolicyReference("TM-CP-100001", "Brindle & Marsh Joinery Ltd", new(2026, 1, 1), new(2026, 12, 31)), policy);
        Assert.Equal("http://legacy.test/policies/tm-cp-100001", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task An_unknown_policy_is_null()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await Lookup(handler).FindAsync("TM-CP-999999", CancellationToken.None));
    }

    [Fact]
    public async Task A_policy_number_is_escaped_so_it_cannot_change_the_request_path()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Lookup(handler).FindAsync("../claims?x=1", CancellationToken.None);

        Assert.Equal("/policies/..%2Fclaims%3Fx%3D1", handler.Requests.Single().RequestUri!.AbsolutePath + handler.Requests.Single().RequestUri!.Query);
    }

    [Fact]
    public async Task An_implausibly_long_policy_number_is_not_sent_at_all()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Null(await Lookup(handler).FindAsync(new string('9', 500), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_legacy_error_is_reported_as_unavailable_not_as_an_unknown_policy(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<LegacySystemUnavailableException>(() => Lookup(handler).FindAsync("TM-CP-100001", CancellationToken.None));
    }

    [Fact]
    public async Task A_network_failure_or_garbled_answer_is_reported_as_unavailable()
    {
        await Assert.ThrowsAsync<LegacySystemUnavailableException>(() =>
            Lookup(new StubHandler(_ => throw new HttpRequestException("refused"))).FindAsync("TM-CP-100001", CancellationToken.None));
        await Assert.ThrowsAsync<LegacySystemUnavailableException>(() =>
            Lookup(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "application/json") }))
                .FindAsync("TM-CP-100001", CancellationToken.None));
    }
}
