using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiProxy.Core.Content;

namespace AiProxy.Tests.Integration;

/// <summary>
/// The compression stage over real HTTP: what the upstream receives, and what the client
/// can fetch back.
/// </summary>
public class ContentCompressionTests : IDisposable
{
    private const string ToolOutput = """
        2026-03-12 10:00:00 INFO worker=api-1 request started
        2026-03-12 10:00:01 INFO worker=api-1 request failed status=503 dur_ms=3110
        2026-03-12 10:00:02 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:03 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:04 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:05 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:06 INFO worker=api-1 nothing to see here
        """;

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"ai-proxy-content-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static string Body(string toolOutput) =>
        $$"""
        {"model":"claude-haiku-4-5","max_tokens":5,"messages":[{"role":"user","content":[{"type":"tool_result","content":{{System.Text.Json.JsonSerializer.Serialize(toolOutput)}}}]}]}
        """;

    private static StringContent Payload(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private Dictionary<string, string> Configuration(Uri upstream) => new()
    {
        ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.ToString(),
        ["PROXY_COMPRESSION"] = "on",
        ["PROXY_COMPRESSION_DB"] = _databasePath,
    };

    [Fact]
    public async Task The_upstream_receives_the_tool_output_smaller()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(Configuration(upstream.BaseAddress));
        var body = Body(ToolOutput);

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        await client.PostAsync("/v1/messages", Payload(body), TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.True(received.Body.Length < body.Length, $"the body was {received.Body.Length} bytes, the original was {body.Length}");
    }

    [Fact]
    public async Task The_rest_of_the_body_reaches_the_upstream_unchanged()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(Configuration(upstream.BaseAddress));
        var body = Body(ToolOutput);

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        await client.PostAsync("/v1/messages", Payload(body), TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.StartsWith(
            """
            {"model":"claude-haiku-4-5","max_tokens":5,"messages":[{"role":"user","content":[{"type":"tool_result","content":"
            """,
            received.Body,
            StringComparison.Ordinal);
        Assert.EndsWith(
            """
            "}]}]}
            """,
            received.Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_response_carries_the_id_the_original_is_kept_under()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(Configuration(upstream.BaseAddress));

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.PostAsync(
            "/v1/messages",
            Payload(Body(ToolOutput)),
            TestContext.Current.CancellationToken);

        Assert.True(response.Headers.TryGetValues("x-ai-proxy-content-id", out var values));
        Assert.NotEmpty(values!);
    }

    [Fact]
    public async Task The_original_is_served_back_under_that_id()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(Configuration(upstream.BaseAddress));
        var body = Body(ToolOutput);

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.PostAsync("/v1/messages", Payload(body), TestContext.Current.CancellationToken);
        var id = response.Headers.GetValues("x-ai-proxy-content-id").First();

        var original = await client.GetStringAsync($"/content/{id}", TestContext.Current.CancellationToken);

        Assert.Equal(body, original);
    }

    [Fact]
    public async Task An_unknown_id_is_answered_with_a_problem()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(Configuration(upstream.BaseAddress));

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.GetAsync("/content/never-stored", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_request_is_forwarded_unchanged_when_the_stage_is_off()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
        });
        var body = Body(ToolOutput);

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        await client.PostAsync("/v1/messages", Payload(body), TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.Equal(body, received.Body);
    }
}
