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

    private sealed class TokenHandler(string token) : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == HealthApi.Path)
            {
                return Task.FromResult(Json("""{"status":"ok","product":"ai-proxy","version":"1.2.3"}"""));
            }

            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(request.Headers.Authorization?.ToString() == $"Bearer {token}" ? Json("{}") : Json("{}", HttpStatusCode.Unauthorized));
        }
    }

    private static async Task<ApiProbe> Probe(HttpMessageHandler handler, string? dataDirectory = null)
    {
        using var client = new HttpClient(handler);
        return await new ApiCheck(client, new Uri("http://127.0.0.1:17432"), "1.2.3", dataDirectory ?? Path.Combine(Path.GetTempPath(), "ai-proxy-no-token-" + Guid.NewGuid().ToString("N"))).ProbeAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_current_proxy_that_refuses_the_token_fails()
    {
        using var directory = new Integration.TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, AiProxy.Hosting.Api.ApiToken.FileName), "stale", TestContext.Current.CancellationToken);
        var handler = new TokenHandler("fresh");

        var probe = await Probe(handler, directory.Path);

        Assert.Equal(CheckStatus.Fail, probe.Result.Status);
        Assert.Contains("token", probe.Result.Detail);
        Assert.Equal(["Bearer stale"], handler.Authorizations);
    }

    [Fact]
    public async Task A_current_proxy_that_accepts_the_token_from_the_data_directory_passes()
    {
        using var directory = new Integration.TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, AiProxy.Hosting.Api.ApiToken.FileName), "fresh", TestContext.Current.CancellationToken);

        var probe = await Probe(new TokenHandler("fresh"), directory.Path);

        Assert.Equal(CheckStatus.Pass, probe.Result.Status);
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
