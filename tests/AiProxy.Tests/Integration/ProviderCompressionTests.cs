using System.Net;
using System.Text;
using System.Text.Json;
using AiProxy.Core.Providers;
using AiProxy.Hosting;

namespace AiProxy.Tests.Integration;

/// <summary>The compression stage over the forward proxy: what a provider upstream receives and what the client gets back.</summary>
public sealed class ProviderCompressionTests : IDisposable
{
    private const string ToolOutput = """
        2026-03-12 10:00:00 INFO worker=api-1 request started
        2026-03-12 10:00:01 INFO worker=api-1 request failed status=503 dur_ms=3110
        2026-03-12 10:00:02 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:03 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:04 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:05 INFO worker=api-1 the second to last line
        2026-03-12 10:00:06 INFO worker=api-1 the last line
        """;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-content-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task The_provider_upstream_receives_the_tool_output_reduced()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(compression: true);
        var body = Body(ToolOutput);

        using var response = await SendAsync(proxy, upstream, body);

        var received = Assert.Single(upstream.Requests);
        Assert.True(received.Body.Length < body.Length, $"the body was {received.Body.Length} bytes, the original was {body.Length}");
        Assert.Contains("status=503", received.Body, StringComparison.Ordinal);
        Assert.Single(received.Body.Split("nothing to see here").Skip(1));
        Assert.Contains("10:00:04", received.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bytes_outside_the_tool_output_are_the_clients_bytes()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(compression: true);
        var body = Body(ToolOutput);

        using var response = await SendAsync(proxy, upstream, body);

        var encoded = JsonSerializer.Serialize(ToolOutput);
        var start = body.IndexOf(encoded, StringComparison.Ordinal);
        var prefix = body[..start];
        var suffix = body[(start + encoded.Length)..];
        var received = Assert.Single(upstream.Requests);
        Assert.NotEqual(body, received.Body);
        Assert.StartsWith(prefix, received.Body, StringComparison.Ordinal);
        Assert.EndsWith(suffix, received.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_client_gets_the_id_the_original_is_kept_under()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(compression: true);
        var body = Body(ToolOutput);

        using var response = await SendAsync(proxy, upstream, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("x-ai-proxy-content-id", out var values));
        var id = Assert.Single(values);
        using var content = new HttpClient { BaseAddress = proxy.BaseAddress };
        Assert.Equal(body, await content.GetStringAsync($"/content/{id}", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_provider_request_is_forwarded_unchanged_when_compression_is_off()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(compression: false);
        var body = Body(ToolOutput);

        using var response = await SendAsync(proxy, upstream, body);

        var received = Assert.Single(upstream.Requests);
        Assert.Equal(body, received.Body);
        Assert.False(response.Headers.Contains("x-ai-proxy-content-id"));
    }

    private static string Body(string toolOutput) =>
        $$"""
        {"model":"claude-haiku-4-5","max_tokens":5,"messages":[{"role":"user","content":[{"type":"tool_result","content":{{JsonSerializer.Serialize(toolOutput)}}}]}]}
        """;

    private Task<ProxyHost> StartProxyAsync(bool compression)
    {
        var configuration = new Dictionary<string, string>();
        if (compression)
        {
            configuration["PROXY_COMPRESSION"] = "on";
            configuration["PROXY_COMPRESSION_DB"] = _databasePath;
        }

        return ProxyHost.StartAsync(configuration, providerHosts: new LoopbackProviderHosts(Dialect.Anthropic));
    }

    private static async Task<HttpResponseMessage> SendAsync(ProxyHost proxy, FakeUpstream upstream, string body)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        return await client.PostAsync(
            new Uri(upstream.BaseAddress, "/v1/messages"),
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
    }
}

/// <summary>Treats loopback hosts as a provider, so a local upstream stands in for a provider API.</summary>
internal sealed class LoopbackProviderHosts(Dialect dialect) : IProviderHosts
{
    public Dialect? Find(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" or "[::1]" ? dialect : null;
}
