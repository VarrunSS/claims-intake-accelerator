using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaimsIntake.Api.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace ClaimsIntake.Api.Tests;

/// <summary>PII-02: HTTP spans carry route templates. A policy number or a claim id in a URL path never reaches a span.</summary>
public class TelemetryScrubbingTests
{
    private static IEnumerable<string> AllTagValues(IEnumerable<Activity> spans) =>
        spans.SelectMany(a => a.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(a.DisplayName));

    [Fact]
    public async Task A_server_span_for_a_claim_carries_the_route_template_and_not_the_claim_id()
    {
        var exported = new List<Activity>();
        using var root = new ApiFactory();
        using var factory = root.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.ConfigureOpenTelemetryTracerProvider((_, tracing) => tracing.AddInMemoryExporter(exported))));
        var client = factory.CreateClient();

        await client.GetAsync($"/claims/{root.Clean.Id}");
        await client.GetAsync($"/claims/{root.Clean.Id}/audit");
        await client.GetAsync("/claims/history?policyNumber=TM-CP-100001"); // 403 for a handler, but still a request that was traced.

        // The in-memory exporter batches by span end; force it out.
        using var provider = factory.Services.GetService<TracerProvider>();
        provider?.ForceFlush();
        var server = exported.Where(a => a.Kind == ActivityKind.Server).ToList();
        Assert.NotEmpty(server);
        var everything = string.Join('\n', AllTagValues(server));
        Assert.DoesNotContain(root.Clean.Id.ToString(), everything);
        Assert.DoesNotContain("TM-CP-100001", everything);
        Assert.Contains(server, a => a.GetTagItem("url.path") as string == "/claims/{id}");
        Assert.Contains(server, a => a.GetTagItem("url.path") as string == "/claims/{id}/audit");
    }

    /// <summary>A server that answers every request with 404, so a real HttpClient span is made (a stub handler would bypass the instrumentation).</summary>
    private static (TcpListener Listener, Task Loop) Serve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var socket = await listener.AcceptTcpClientAsync();
                    var stream = socket.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // Stopped.
            }
        });
        return (listener, loop);
    }

    [Fact]
    public async Task A_client_span_for_a_policy_lookup_carries_the_route_template_and_not_the_policy_number()
    {
        var exported = new List<Activity>();
        var (listener, loop) = Serve();
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddHttpClientInstrumentation(TelemetryExtensions.ScrubHttpClientSpan)
            .AddInMemoryExporter(exported)
            .Build();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using (var client = new HttpClient())
        {
            await client.GetAsync($"http://127.0.0.1:{port}/policies/TM-CP-100001?x=Smith");
            await client.GetAsync($"http://127.0.0.1:{port}/claims/history?policyNumber=TM-CP-100002");
        }

        tracing.ForceFlush();
        listener.Stop();
        await loop;
        Assert.NotEmpty(exported);
        var everything = string.Join('\n', AllTagValues(exported));
        Assert.DoesNotContain("TM-CP-100001", everything);
        Assert.DoesNotContain("TM-CP-100002", everything);
        Assert.DoesNotContain("Smith", everything);
        Assert.Contains(exported, a => a.GetTagItem("url.path") as string == "/policies/{number}");
        Assert.Contains(exported, a => (a.GetTagItem("url.full") as string)?.EndsWith("/policies/{number}") == true);
    }
}
