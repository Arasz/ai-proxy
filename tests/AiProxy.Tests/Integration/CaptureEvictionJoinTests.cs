using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AiProxy.Cli;
using AiProxy.Client;
using AiProxy.Core.Providers;
using AiProxy.Hosting.Proxy;
using AiProxy.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console.Testing;

namespace AiProxy.Tests.Integration;

/// <summary>A real capture store at its cap, behind the real host: what an eviction leaves for <c>status</c> and for <c>proxy.log</c>.</summary>
public sealed class CaptureEvictionJoinTests
{
    private const long CapBytes = 128 * 1024;
    private const int BodyChars = 50_000;
    private const int Exchanges = 6;
    private const int CaptureEvictedEventId = 1504;

    [Fact]
    public async Task Status_shows_the_last_eviction_time_of_a_real_store_that_evicted()
    {
        using var directory = new TemporaryDirectory();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartCappedProxyAsync(logging: null);
        await PushExchangesAsync(proxy, upstream);
        var store = proxy.Services.GetRequiredService<SqliteCaptureStore>();
        using var dummy = new DummyOwner();
        var fake = dummy.Info();
        var info = new ProxyControl.ProxyInfo(fake.Product, fake.Protocol, fake.Nonce, fake.Pid, fake.StartUtc, fake.ProxyUrl, fake.ApiUrls, fake.ProcessStartToken!);
        await using var control = new ProxyControl(directory.Path, info, new StubLifetime(),
            async token => new ProxyControl.CaptureInfo(true, await store.GetStateAsync(token)));
        await control.Start(StartupLease.NoOpLease);

        var status = await RunStatusAsync(directory.Path);

        using var json = JsonDocument.Parse(status);
        var state = json.RootElement.GetProperty("capture").GetProperty("state");
        Assert.NotEqual(JsonValueKind.Null, state.GetProperty("lastEvictionAt").ValueKind);
    }

    [Fact]
    public async Task An_eviction_writes_its_event_id_to_proxy_log_in_the_data_directory()
    {
        using var logDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(logDirectory.Path);
        await using var upstream = await FakeUpstream.StartAsync();
        var path = Path.Combine(logDirectory.Path, ProxyLogFile.FileName);
        await using (var proxy = await StartCappedProxyAsync(ProxyLogFile.ForDirectory(logDirectory.Path)))
        {
            await PushExchangesAsync(proxy, upstream);
        }

        var text = File.Exists(path) ? await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken) : "";

        Assert.Contains($" {CaptureEvictedEventId} ", text, StringComparison.Ordinal);
    }

    private static Task<ProxyHost> StartCappedProxyAsync(ILoggerProvider? logging)
    {
        var configuration = new Dictionary<string, string>
        {
            ["Proxy:Capture"] = "true",
            ["Proxy:CaptureMaxBytes"] = CapBytes.ToString(CultureInfo.InvariantCulture)
        };
        return ProxyHost.StartAsync(configuration, providerHosts: new LoopbackProviderHosts(Dialect.Anthropic), logging: logging);
    }

    private static async Task PushExchangesAsync(ProxyHost proxy, FakeUpstream upstream)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        for (var index = 0; index < Exchanges; index++)
        {
            var body = $$"""{"model":"m","messages":[{"role":"user","content":"{{new string((char)('a' + index), BodyChars)}}"}]}""";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri(upstream.BaseAddress, "/v1/messages"), content, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task<string> RunStatusAsync(string directory)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var console = new TestConsole();
        using var http = new HttpClient();
        var runtime = new GainRuntime(console, output, error, new ProxyClient(http), "/work", false, new FakeTimeProvider(DateTimeOffset.UtcNow), null, _ => null);
        var exit = await new CliApplication(runtime).RunAsync(["status", "--data-directory", directory, "-f", "json"], TestContext.Current.CancellationToken);
        Assert.True(exit == 0, error.ToString());
        return output + console.Output;
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
