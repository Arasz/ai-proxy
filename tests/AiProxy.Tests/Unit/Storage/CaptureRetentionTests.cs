using AiProxy.Core.Capture;
using AiProxy.Core.Providers;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>How long the capture file keeps rows, and the ceiling above which capture stops.</summary>
public sealed class CaptureRetentionTests : IDisposable
{
    private const int CeilingBytes = 64 * 1024;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ai-proxy-tests", Guid.CreateVersion7().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger _logger = new();
    private readonly SqliteCaptureStore _store;

    public CaptureRetentionTests()
    {
        _store = new SqliteCaptureStore(DatabasePath, CeilingBytes, _time, _logger);
    }

    private string DatabasePath => Path.Combine(_directory, SqliteCaptureStore.FileName);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private CapturedRequest Capture(string id, int bodyBytes, int daysAgo = 0)
    {
        return new CapturedRequest(id, _time.GetUtcNow() - TimeSpan.FromDays(daysAgo), Dialect.Anthropic, "api.anthropic.com", "/v1/messages", new byte[bodyBytes]);
    }

    private SqliteConnection Open()
    {
        return ContentSchema.Open(DatabasePath);
    }

    [Fact]
    public async Task Capture_stops_once_the_live_pages_pass_the_ceiling_and_logs_it_once()
    {
        var token = TestContext.Current.CancellationToken;
        await _store.StoreAsync(Capture("r1", 100_000), token);

        await _store.StoreAsync(Capture("r2", 10), token);
        await _store.StoreAsync(Capture("r3", 10), token);

        using var connection = Open();
        Assert.Equal(["r1"], connection.Query<string>("SELECT request_id FROM capture_request;").ToList());
        Assert.Equal(1, _logger.Messages.Count(message => message.Contains("ceiling", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Capture_resumes_after_an_eviction_pass_frees_pages_even_though_the_file_stays_large()
    {
        var token = TestContext.Current.CancellationToken;
        await _store.StoreAsync(Capture("old", 100_000, daysAgo: 15), token);
        await _store.StoreAsync(Capture("refused", 10), token);
        var fileLengthBefore = new FileInfo(DatabasePath).Length;

        var removed = await _store.EvictAsync(token);
        await _store.StoreAsync(Capture("resumed", 10), token);

        using var connection = Open();
        Assert.Equal(1, removed);
        Assert.Equal(["resumed"], connection.Query<string>("SELECT request_id FROM capture_request;").ToList());
        Assert.True(new FileInfo(DatabasePath).Length >= fileLengthBefore - 4096);
    }

    [Fact]
    public async Task A_pass_removes_requests_and_responses_older_than_fourteen_days_and_keeps_the_newer_ones()
    {
        var token = TestContext.Current.CancellationToken;
        await _store.StoreAsync(Capture("new", 10, daysAgo: 13), token);
        await _store.StoreAsync(Capture("old", 10, daysAgo: 15), token);
        foreach (var (id, days) in new[] { ("new", 13), ("old", 15) })
        {
            await _store.StoreResponseAsync(
                new CapturedResponse(id, _time.GetUtcNow() - TimeSpan.FromDays(days), 200, 1, 1, 1, null, null, []),
                token);
        }

        await _store.EvictAsync(token);

        using var connection = Open();
        Assert.Equal(["new"], connection.Query<string>("SELECT request_id FROM capture_request;").ToList());
        Assert.Equal(["new"], connection.Query<string>("SELECT request_id FROM capture_response;").ToList());
    }

    [Fact]
    public async Task The_capture_file_has_indexes_on_the_time_columns()
    {
        await _store.StoreAsync(Capture("r1", 10), TestContext.Current.CancellationToken);

        using var connection = Open();
        var indexes = connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'index';").ToList();
        Assert.Contains("capture_request_captured", indexes);
        Assert.Contains("capture_response_completed", indexes);
    }

    [Fact]
    public async Task A_pass_on_a_capture_file_that_was_never_written_removes_nothing_and_creates_nothing()
    {
        var removed = await _store.EvictAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.False(File.Exists(DatabasePath));
    }

    private sealed class RecordingLogger : ILogger<SqliteCaptureStore>
    {
        public List<string> Messages { get; } = [];

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
            Messages.Add(formatter(state, exception));
        }
    }
}
