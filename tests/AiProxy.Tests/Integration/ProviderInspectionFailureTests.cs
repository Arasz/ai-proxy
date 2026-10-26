using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiProxy.Ccr;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using Microsoft.Extensions.Logging;

namespace AiProxy.Tests.Integration;

/// <summary>What the proxy does when inspection cannot or should not run.</summary>
public sealed class ProviderInspectionFailureTests
{
    private const string Body = """{"model":"m","messages":[{"role":"user","content":"hello"}]}""";

    [Fact]
    public async Task A_failing_compression_pipeline_forwards_the_original_body_and_logs_the_failure()
    {
        var loggers = new RecordingLoggerFactory();
        using var directory = new TemporaryDirectory();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(new ThrowingCompressionPipeline(), loggers, directory.Path);

        using var response = await SendAsync(proxy, upstream, new StringContent(Body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.Single(upstream.Requests);
        Assert.Equal(Body, received.Body);
        Assert.Contains(loggers.Entries, entry =>
            entry.Category == typeof(ProviderRequestInspector).FullName &&
            entry is { Level: LogLevel.Warning, Exception: InvalidOperationException });
    }

    [Fact]
    public async Task A_body_of_unknown_length_is_forwarded_without_compression()
    {
        using var directory = new TemporaryDirectory();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(new MarkerCompressionPipeline(), new RecordingLoggerFactory(), directory.Path);
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.Address), UseProxy = true };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(upstream.BaseAddress, "/v1/messages"))
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json")
        };
        request.Headers.TransferEncodingChunked = true;

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.Equal(Body, received.Body);
    }

    [Fact]
    public async Task A_body_of_known_length_is_compressed()
    {
        using var directory = new TemporaryDirectory();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(new MarkerCompressionPipeline(), new RecordingLoggerFactory(), directory.Path);

        using var response = await SendAsync(proxy, upstream, new StringContent(Body, Encoding.UTF8, "application/json"));

        var received = Assert.Single(upstream.Requests);
        Assert.Equal(Body + MarkerCompressionPipeline.Marker, received.Body);
    }

    [Fact]
    public async Task Proxy_failures_reach_the_host_logger()
    {
        var loggers = new RecordingLoggerFactory();
        var closedPort = FreePort();
        using var directory = new TemporaryDirectory();
        await using var service = await StartProxyAsync(new PassThroughCompressionPipeline(), loggers, directory.Path);
        using var handler = new HttpClientHandler { Proxy = new WebProxy(service.Address), UseProxy = true };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync($"http://127.0.0.1:{closedPort}/", TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(loggers.Entries, entry => entry.Category.StartsWith("Titanium.Web.Proxy", StringComparison.Ordinal) && entry.Exception is not null);
    }

    private static async Task<TitaniumProxyService> StartProxyAsync(ICompressionPipeline pipeline, RecordingLoggerFactory loggers, string dataDirectory)
    {
        var hosts = new LoopbackProviderHosts(Dialect.Anthropic);
        var service = new TitaniumProxyService(
            new ProxyRuntimeOptions(IPAddress.Loopback, FreePort(), false, false, dataDirectory),
            new ProviderRequestInspector(pipeline, hosts, new NoRequestCapture(), new CcrOff(), loggers.CreateLogger<ProviderRequestInspector>(), TimeProvider.System, new NoGainLedger()),
            hosts,
            loggers);
        await service.StartAsync(TestContext.Current.CancellationToken);
        return service;
    }

    private static async Task<HttpResponseMessage> SendAsync(TitaniumProxyService proxy, FakeUpstream upstream, HttpContent content)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.Address), UseProxy = true };
        using var client = new HttpClient(handler);
        return await client.PostAsync(new Uri(upstream.BaseAddress, "/v1/messages"), content, TestContext.Current.CancellationToken);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ThrowingCompressionPipeline : ICompressionPipeline
    {
        public ValueTask<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("pipeline broke");
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName)
        {
            return new RecordingLogger(categoryName, _entries);
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(new LogEntry(category, logLevel, exception));
        }
    }
}