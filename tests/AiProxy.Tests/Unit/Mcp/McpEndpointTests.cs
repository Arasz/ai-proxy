using System.Net;
using AiProxy.Cli;
using AiProxy.Mcp;
using AiProxy.Tests.Unit.Cli;

namespace AiProxy.Tests.Unit.Mcp;

public sealed class McpEndpointTests
{
    [Fact]
    public async Task Invalid_endpoint_message_has_no_parameter_suffix()
    {
        using var fixture = new GainCommandTests.Fixture();
        var exit = await new CliApplication(fixture.Runtime).RunAsync(["mcp", "--endpoint", "not-a-url"], TestContext.Current.CancellationToken);
        Assert.Equal(1, exit);
        Assert.Contains("--endpoint must be an absolute HTTP or HTTPS proxy URL.", fixture.Error.ToString());
        Assert.DoesNotContain("Parameter", fixture.Error.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Probe_reports_whether_the_health_endpoint_answers_ok(HttpStatusCode status, bool expected)
    {
        using var handler = new Handler(_ => new HttpResponseMessage(status));
        Assert.Equal(expected, await McpServerProcess.IsReachableAsync("http://127.0.0.1:1/", handler, TestContext.Current.CancellationToken));
        Assert.Equal("http://127.0.0.1:1/health", handler.Seen.Single().ToString());
    }

    [Fact]
    public async Task Probe_reports_unreachable_when_the_connection_fails()
    {
        using var handler = new Handler(_ => throw new HttpRequestException("refused"));
        Assert.False(await McpServerProcess.IsReachableAsync("http://127.0.0.1:1/", handler, TestContext.Current.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
