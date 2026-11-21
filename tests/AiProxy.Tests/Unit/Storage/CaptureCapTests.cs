using AiProxy.Core.Capture;
using AiProxy.Core.Providers;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>Capture at its cap: the oldest whole exchange goes, and a write bigger than the cap is refused.</summary>
public sealed class CaptureCapTests : IDisposable
{
    private const int CapBytes = 128 * 1024;
    private const int BodyBytes = 50_000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ai-proxy-tests", Guid.CreateVersion7().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteCaptureStore _store;

    public CaptureCapTests()
    {
        _store = new SqliteCaptureStore(DatabasePath, CapBytes, _time, NullLogger<SqliteCaptureStore>.Instance);
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

    private CapturedRequest Request(string id, int bodyBytes)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        return new CapturedRequest(id, _time.GetUtcNow(), Dialect.Anthropic, "api.anthropic.com", "/v1/messages", new byte[bodyBytes]);
    }

    private CapturedResponse Response(string id)
    {
        return new CapturedResponse(id, _time.GetUtcNow(), 200, 1, 1, 1, null, null, []);
    }

    private List<string> Ids(string table)
    {
        using var connection = ContentSchema.Open(DatabasePath);
        return connection.Query<string>($"SELECT request_id FROM {table} ORDER BY request_id;").ToList();
    }

    [Fact]
    public async Task At_the_cap_a_new_write_succeeds_and_the_oldest_exchange_goes_with_both_its_rows()
    {
        var token = TestContext.Current.CancellationToken;
        await _store.StoreAsync(Request("r1", BodyBytes), token);
        await _store.StoreResponseAsync(Response("r1"), token);
        await _store.StoreAsync(Request("r2", BodyBytes), token);
        await _store.StoreResponseAsync(Response("r2"), token);

        await _store.StoreAsync(Request("r3", BodyBytes), token);
        await _store.StoreResponseAsync(Response("r3"), token);

        Assert.DoesNotContain("r1", Ids("capture_request"));
        Assert.DoesNotContain("r1", Ids("capture_response"));
        Assert.Contains("r3", Ids("capture_request"));
        Assert.Contains("r3", Ids("capture_response"));
        var state = await _store.GetStateAsync(token);
        Assert.True(state.LiveBytes <= state.CapBytes);
        Assert.Equal(_time.GetUtcNow(), state.LastEvictionAt);
    }

    [Fact]
    public async Task A_response_whose_request_was_evicted_is_not_stored()
    {
        var token = TestContext.Current.CancellationToken;
        await _store.StoreAsync(Request("r1", BodyBytes), token);
        await _store.StoreAsync(Request("r2", BodyBytes), token);
        await _store.StoreAsync(Request("r3", BodyBytes), token);

        await _store.StoreResponseAsync(Response("r1"), token);

        Assert.Empty(Ids("capture_response"));
    }

    [Fact]
    public async Task A_write_bigger_than_the_cap_is_rejected_evicts_nothing_and_is_the_last_write_error()
    {
        var token = TestContext.Current.CancellationToken;
        await _store.StoreAsync(Request("r1", BodyBytes), token);

        await _store.StoreAsync(Request("huge", CapBytes + 1), token);

        Assert.Equal(["r1"], Ids("capture_request"));
        var state = await _store.GetStateAsync(token);
        Assert.NotNull(state.LastWriteError);
        Assert.Null(state.LastEvictionAt);
    }

    [Fact]
    public async Task The_state_reports_size_cap_and_oldest_row_and_no_error_on_a_healthy_file()
    {
        var token = TestContext.Current.CancellationToken;
        var first = Request("r1", 100);
        await _store.StoreAsync(first, token);

        var state = await _store.GetStateAsync(token);

        Assert.Equal(CapBytes, state.CapBytes);
        Assert.True(state.LiveBytes > 0);
        Assert.Equal(first.CapturedAt, state.OldestRowAt);
        Assert.Null(state.LastWriteError);
        Assert.Null(state.LastEvictionAt);
    }

    [Fact]
    public async Task The_age_prune_sets_the_last_eviction_time()
    {
        var token = TestContext.Current.CancellationToken;
        await _store.StoreAsync(Request("old", 100), token);
        _time.Advance(TimeSpan.FromDays(15));

        await _store.EvictAsync(token);

        var state = await _store.GetStateAsync(token);
        Assert.Equal(_time.GetUtcNow(), state.LastEvictionAt);
        Assert.Null(state.OldestRowAt);
    }
}
