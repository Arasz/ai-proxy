using System.Net;
using System.Text;
using AiProxy.Core.Doctor;
using AiProxy.Doctor;

namespace AiProxy.Tests.Unit.Doctor;

public sealed class ApiCheckTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond());
        }
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused");
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static Task<ApiProbe> Probe(HttpMessageHandler handler)
    {
        using var client = new HttpClient(handler);
        return new ApiCheck(client, new Uri("http://127.0.0.1:17432"), "1.2.3").ProbeAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_matching_product_and_version_passes()
    {
        var probe = await Probe(new StubHandler(() => Json("""{"status":"ok","product":"ai-proxy","version":"1.2.3"}""")));
        Assert.Equal(CheckStatus.Pass, probe.Result.Status);
        Assert.True(probe.Reachable);
    }

    [Fact]
    public async Task A_different_version_warns()
    {
        var probe = await Probe(new StubHandler(() => Json("""{"status":"ok","product":"ai-proxy","version":"1.0.0"}""")));
        Assert.Equal(CheckStatus.Warn, probe.Result.Status);
        Assert.Contains("1.0.0", probe.Result.Detail);
    }

    [Fact]
    public async Task An_older_proxy_without_product_is_unknown()
    {
        var probe = await Probe(new StubHandler(() => Json("""{"status":"ok"}""")));
        Assert.Equal(CheckStatus.Unknown, probe.Result.Status);
    }

    [Theory]
    [InlineData("""{"status":"ok","product":"something-else","version":"1.2.3"}""")]
    [InlineData("<html>hello</html>")]
    public async Task A_foreign_server_fails_but_is_reachable(string body)
    {
        var probe = await Probe(new StubHandler(() => Json(body)));
        Assert.Equal(CheckStatus.Fail, probe.Result.Status);
        Assert.True(probe.Reachable);
    }

    [Theory]
    [InlineData("""{"status":"ok","product":1}""")]
    [InlineData("""{"status":"ok","product":["ai-proxy"]}""")]
    public async Task A_non_string_product_fails_without_crashing(string body)
    {
        var probe = await Probe(new StubHandler(() => Json(body)));
        Assert.Equal(CheckStatus.Fail, probe.Result.Status);
    }

    [Fact]
    public async Task A_non_string_version_is_treated_as_missing()
    {
        var probe = await Probe(new StubHandler(() => Json("""{"status":"ok","product":"ai-proxy","version":2}""")));
        Assert.Equal(CheckStatus.Warn, probe.Result.Status);
    }

    [Fact]
    public async Task An_unreachable_endpoint_fails_and_is_not_reachable()
    {
        var probe = await Probe(new RefusingHandler());
        Assert.Equal(CheckStatus.Fail, probe.Result.Status);
        Assert.False(probe.Reachable);
    }
}
