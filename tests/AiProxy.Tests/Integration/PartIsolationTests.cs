using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiProxy.Tests.Integration;

/// <summary>One reducer failing, through the composed host: the parts that reduce still reduce, and the failure is logged.</summary>
public sealed class PartIsolationTests : IDisposable
{
    private const string Log = """
        2026-03-12 10:00:00 INFO worker=api-1 request started
        2026-03-12 10:00:01 INFO worker=api-1 request failed status=503 dur_ms=3110
        2026-03-12 10:00:02 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:03 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:04 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:05 INFO worker=api-1 the last line
        """;

    private const string Prose = """
        the same line said again
        the same line said again
        the same line said again
        the same line said again
        the line after it
        """;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-isolation-{Guid.NewGuid():N}.db");

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

    [Fact]
    public async Task A_part_whose_handler_throws_does_not_stop_the_other_parts_from_reducing()
    {
        var logs = new RecordingLoggerProvider();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(logs);
        var body = $$"""
            {"model":"m","messages":[{"role":"user","content":[{"type":"tool_result","content":{{JsonSerializer.Serialize(Log)}}},{"type":"tool_result","content":{{JsonSerializer.Serialize(Prose)}}}]}]}
            """;

        await SendAsync(proxy, upstream, body);

        var received = Assert.Single(upstream.Requests).Body;
        Assert.Contains(JsonSerializer.Serialize(Log), received, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(Prose), received, StringComparison.Ordinal);
        Assert.True(received.Length < body.Length, $"the body was {received.Length} bytes, the original was {body.Length}");
        AssertOnePartFailureLogged(logs);
    }

    [Fact]
    public async Task A_part_failure_is_logged_even_when_nothing_else_in_the_request_changed()
    {
        var logs = new RecordingLoggerProvider();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(logs);
        var body = $$"""
            {"model":"m","messages":[{"role":"user","content":[{"type":"tool_result","content":{{JsonSerializer.Serialize(Log)}}}]}]}
            """;

        await SendAsync(proxy, upstream, body);

        Assert.Equal(body, Assert.Single(upstream.Requests).Body);
        AssertOnePartFailureLogged(logs);
    }

    private Task<ProxyHost> StartProxyAsync(RecordingLoggerProvider logs) =>
        ProxyHost.StartAsync(
            new Dictionary<string, string>
            {
                ["PROXY_COMPRESSION"] = "on",
                ["PROXY_COMPRESSION_DB"] = _databasePath,
                ["PROXY_COMPRESSION_MINIMUM"] = "1",
            },
            providerHosts: new LoopbackProviderHosts(Dialect.Anthropic),
            configure: services =>
            {
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(LogCompressionHandler)));
                services.AddSingleton<ICompressionHandler, ThrowingLogHandler>();
                services.AddSingleton<ILoggerProvider>(logs);
            });

    private static async Task SendAsync(ProxyHost proxy, FakeUpstream upstream, string body)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        using var response = await client.PostAsync(
            new Uri(upstream.BaseAddress, "/v1/messages"),
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
    }

    private static void AssertOnePartFailureLogged(RecordingLoggerProvider logs)
    {
        var failure = Assert.Single(logs.Entries, entry => entry.EventId == 1402);
        Assert.Equal(typeof(ProviderRequestInspector).FullName, failure.Category);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(failure.Exception).Message);
    }

    private sealed class ThrowingLogHandler : ICompressionHandler
    {
        public bool CanHandle(ContentType type) => type == ContentType.Log;

        public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private sealed record LogEntry(string Category, LogLevel Level, int EventId, Exception? Exception);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId.Id, exception));
    }
}
