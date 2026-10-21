using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using AiProxy.Core.Compression;
using AiProxy.Core.Providers;

namespace AiProxy.Tests.Integration;

/// <summary>
/// The proxy over real HTTP: what a provider request reaches, what a pass-through request
/// reaches, and what the caller gets back.
/// </summary>
public class ProxyingTests
{
    private const string AnthropicBody = """{"model":"claude-haiku-4-5","max_tokens":5,"messages":[]}""";
    private const string OpenAiBody = """{"model":"gpt-4o","max_completion_tokens":5,"messages":[]}""";

    [Fact]
    public async Task A_provider_request_reaches_the_upstream()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.PostAsync("/v1/messages", Body(AnthropicBody), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var received = Assert.Single(upstream.Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/v1/messages", received.Path);
        Assert.Equal(AnthropicBody, received.Body);
    }

    [Fact]
    public async Task The_upstream_received_the_callers_credential_for_a_path_named_provider()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
            ["PROXY_ANTHROPIC_KEY"] = "PROXY-KEY",
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = Body(AnthropicBody),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", "CALLER-KEY");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.Equal("CALLER-KEY", received.Headers["x-api-key"]);
    }

    [Fact]
    public async Task The_upstream_received_the_proxys_credential_for_a_body_detected_provider()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
            ["PROXY_ANTHROPIC_KEY"] = "PROXY-KEY",
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var request = new HttpRequestMessage(HttpMethod.Post, "/gateway/invoke")
        {
            Content = Body(AnthropicBody),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", "CALLER-KEY");
        request.Headers.TryAddWithoutValidation("authorization", "Bearer CALLER-TOKEN");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.Equal("PROXY-KEY", received.Headers["x-api-key"]);
        Assert.False(received.Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task A_body_detected_provider_with_no_proxy_credential_is_refused()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.PostAsync("/gateway/invoke", Body(AnthropicBody), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("routing_credential_ambiguous", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task A_conflicting_request_is_refused_and_no_upstream_sees_it()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
            ["PROXY_ANTHROPIC_KEY"] = "PROXY-KEY",
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.PostAsync("/v1/messages", Body(OpenAiBody), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("routing_credential_ambiguous", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task A_pass_through_request_reaches_the_pass_through_upstream_unchanged()
    {
        await using var provider = await FakeUpstream.StartAsync();
        await using var mcp = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = provider.BaseAddress.ToString(),
            ["PROXY_PASSTHROUGH"] = $"/mcp={mcp.BaseAddress}",
        });

        const string jsonRpc = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.PostAsync("/mcp", Body(jsonRpc), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var received = Assert.Single(mcp.Requests);
        Assert.Equal("/mcp", received.Path);
        Assert.Equal(jsonRpc, received.Body);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task An_unknown_path_is_refused()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.GetAsync("/nothing-here", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("routing_unknown", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task The_hop_by_hop_headers_are_dropped_and_the_callers_headers_are_kept()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = Body(AnthropicBody),
        };
        request.Headers.TryAddWithoutValidation("x-custom", "hello");
        request.Headers.TryAddWithoutValidation("accept-encoding", "gzip");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.Equal("hello", received.Headers["x-custom"]);
        Assert.False(received.Headers.ContainsKey("accept-encoding"));
    }

    [Fact]
    public async Task The_method_and_the_query_string_arrive_unchanged()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        await client.PostAsync("/v1/messages?beta=true", Body(AnthropicBody), TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/v1/messages", received.Path);
        Assert.Equal("?beta=true", received.QueryString);
    }

    [Fact]
    public async Task The_upstreams_status_and_body_reach_the_caller()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static (_, context) =>
        {
            context.Response.StatusCode = 429;
            return context.Response.WriteAsync("slow down");
        };

        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.PostAsync("/v1/messages", Body(AnthropicBody), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("slow down", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_local_endpoint_still_answers_locally()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task A_provider_request_goes_through_the_compression_pipeline()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(
            new Dictionary<string, string>
            {
                ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
            },
            new MarkerCompressionPipeline());

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        await client.PostAsync("/v1/messages", Body(AnthropicBody), TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.Equal(AnthropicBody + MarkerCompressionPipeline.Marker, received.Body);
    }

    [Fact]
    public async Task A_pass_through_request_does_not_go_through_the_compression_pipeline()
    {
        await using var provider = await FakeUpstream.StartAsync();
        await using var mcp = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(
            new Dictionary<string, string>
            {
                ["PROXY_ANTHROPIC_UPSTREAM"] = provider.BaseAddress.ToString(),
                ["PROXY_PASSTHROUGH"] = $"/mcp={mcp.BaseAddress}",
            },
            new MarkerCompressionPipeline());

        const string jsonRpc = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        await client.PostAsync("/mcp", Body(jsonRpc), TestContext.Current.CancellationToken);

        var received = Assert.Single(mcp.Requests);
        Assert.Equal(jsonRpc, received.Body);
    }

    [Fact]
    public async Task A_streamed_response_is_relayed_chunk_by_chunk()
    {
        // The upstream sends one chunk, waits, and sends another. The caller must see
        // the first chunk while the upstream is still producing, so a forwarder that
        // buffers the response to its end cannot pass this.
        var upstreamFinished = new TaskCompletionSource();

        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: one\n\n", TestContext.Current.CancellationToken);
            await context.Response.Body.FlushAsync(TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            await context.Response.WriteAsync("data: two\n\n", TestContext.Current.CancellationToken);
            await context.Response.Body.FlushAsync(TestContext.Current.CancellationToken);
            upstreamFinished.TrySetResult();
        };

        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["PROXY_ANTHROPIC_UPSTREAM"] = upstream.BaseAddress.ToString(),
        });

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = Body(AnthropicBody),
        };

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.False(upstreamFinished.Task.IsCompleted, "the first chunk arrived only after the upstream finished");
        Assert.Equal("data: one\n\n", Encoding.UTF8.GetString(buffer, 0, read));
    }

    private static StringContent Body(string body) => new(body, Encoding.UTF8, "application/json");
}
