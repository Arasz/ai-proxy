using System.Net;
using System.Text;
using AiProxy.Client;
using AiProxy.Hosting.Api;
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

    [Fact]
    public async Task A_401_names_the_token_file_the_directory_rule_and_a_restart()
    {
        using var directory = new Integration.TemporaryDirectory();
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        var result = await NewTools(http, directory.Path).RetrieveOriginal("0123456789abcdef", "whole", cancellationToken: TestContext.Current.CancellationToken);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError);
        Assert.Contains("HTTP 401", text, StringComparison.Ordinal);
        Assert.Contains($"no API token at {Path.Combine(directory.Path, ApiToken.FileName)}", text, StringComparison.Ordinal);
        Assert.Contains("Proxy__DataDirectory", text, StringComparison.Ordinal);
        Assert.Contains("restart", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--endpoint points", text, StringComparison.Ordinal);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_token_file_is_a_tool_error_not_a_crash()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "File modes are POSIX only.");
        using var directory = new Integration.TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, ApiToken.FileName);
        await File.WriteAllTextAsync(path, "secret-token-value", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(path, UnixFileMode.None);
        using var http = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("no request expected")));

        var result = await NewTools(http, directory.Path).RetrieveOriginal("0123456789abcdef", "whole", cancellationToken: TestContext.Current.CancellationToken);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError);
        Assert.Contains($"Cannot read the API token file {path}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-value", text, StringComparison.Ordinal);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static RetrievalTools NewTools(HttpClient http, string? dataDirectory = null) => new(new ProxyClient(http), new McpServerOptions("http://127.0.0.1/", dataDirectory ?? Path.Combine(Path.GetTempPath(), "ai-proxy-no-token-" + Guid.NewGuid().ToString("N"))));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}