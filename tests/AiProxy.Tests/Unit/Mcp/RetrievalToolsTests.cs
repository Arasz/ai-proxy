using System.Net;
using System.Text;
using AiProxy.Client;
using AiProxy.Mcp;
using ModelContextProtocol.Protocol;

namespace AiProxy.Tests.Unit.Mcp;

public sealed class RetrievalToolsTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Invalid_proxy_response_is_an_actionable_tool_error(string body)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }))) { BaseAddress = new Uri("http://127.0.0.1/") };
        var result = await NewTools(http).RetrieveOriginal("0123456789abcdef", "whole", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("proxy", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var http = new HttpClient(new StubHandler((_, token) => Task.FromCanceled<HttpResponseMessage>(token))) { BaseAddress = new Uri("http://127.0.0.1/") };
        var tools = NewTools(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.RetrieveOriginal("0123456789abcdef", "whole", cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task Proxy_timeout_is_an_actionable_tool_error()
    {
        using var http = new HttpClient(new StubHandler((_, _) => throw new TaskCanceledException("timeout"))) { BaseAddress = new Uri("http://127.0.0.1/") };
        var result = await NewTools(http).RetrieveOriginal("0123456789abcdef", "whole", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("timed out", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private static RetrievalTools NewTools(HttpClient http)
    {
        return new RetrievalTools(new ProxyClient(http), new McpServerOptions("http://127.0.0.1/"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return send(request, cancellationToken);
        }
    }
}