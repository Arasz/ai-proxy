using AiProxy.Core.Capture;
using AiProxy.Core.Timing;
using AiProxy.Core.Providers;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>The capture store, tested against a real database file.</summary>
public sealed class CaptureStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ai-proxy-tests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly SqliteCaptureStore _store;

    public CaptureStoreTests()
    {
        _databasePath = Path.Combine(_directory, "data", SqliteCaptureStore.FileName);
        _store = new SqliteCaptureStore(_databasePath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_capture_keeps_the_body_bytes_exactly_and_where_the_request_went()
    {
        // Bytes that are not valid UTF-8 would not survive a round trip through text.
        byte[] body = [0x7B, 0xFF, 0xFE, 0x00, 0xC3, 0x28, 0x7D];
        var capturedAt = new DateTimeOffset(2026, 10, 23, 12, 30, 0, TimeSpan.Zero);

        await _store.StoreAsync(
            new CapturedRequest("r1", capturedAt, Dialect.Gemini, "generativelanguage.googleapis.com", "/v1beta/models/x:generateContent", body),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await ReadRowsAsync());
        Assert.Equal("r1", row.RequestId);
        Assert.Equal(capturedAt, DateTimeOffset.Parse(row.CapturedAt, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Gemini", row.Provider);
        Assert.Equal("generativelanguage.googleapis.com", row.Host);
        Assert.Equal("/v1beta/models/x:generateContent", row.Path);
        Assert.Equal(body, row.Body);
    }

    [Fact]
    public async Task Every_capture_is_its_own_row()
    {
        await _store.StoreAsync(Capture("r1"), TestContext.Current.CancellationToken);
        await _store.StoreAsync(Capture("r2"), TestContext.Current.CancellationToken);

        var rows = await ReadRowsAsync();

        Assert.Equal(["r1", "r2"], rows.Select(row => row.RequestId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_capture_file_and_its_directory_are_readable_by_the_user_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not apply on Windows.");

        await _store.StoreAsync(Capture("r1"), TestContext.Current.CancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_databasePath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(_databasePath)!));
        }
    }

    [Fact]
    public async Task A_capture_file_that_was_open_to_others_is_tightened()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not apply on Windows.");
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await File.WriteAllBytesAsync(_databasePath, [], TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        await _store.StoreAsync(Capture("r1"), TestContext.Current.CancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_databasePath));
        }
    }

    [Fact]
    public async Task A_capture_directory_that_was_open_to_others_is_tightened()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not apply on Windows.");
        var directory = Path.GetDirectoryName(_databasePath)!;
        if (!OperatingSystem.IsWindows())
        {
            var openToOthers = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, openToOthers);
            Assert.Equal(openToOthers, File.GetUnixFileMode(directory));
        }

        await _store.StoreAsync(Capture("r1"), TestContext.Current.CancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }
    }

    [Fact]
    public async Task A_response_keeps_its_numbers_and_its_stages_in_microseconds()
    {
        var completedAt = new DateTimeOffset(2026, 10, 23, 12, 30, 1, TimeSpan.Zero);
        StageTiming[] stages = [new("body-read", TimeSpan.FromTicks(15)), new("compression.reduce.Log", TimeSpan.FromMilliseconds(2.5)), new("upstream.ttfb", TimeSpan.FromSeconds(1))];

        await _store.StoreResponseAsync(
            new CapturedResponse("r1", completedAt, 200, 1000, 600, 321, new ResponseUsage(11, 22, 33, 44), null, stages),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await ReadResponsesAsync());
        Assert.Equal("r1", row.RequestId);
        Assert.Equal(completedAt, DateTimeOffset.Parse(row.CompletedAt, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(200, row.Status);
        Assert.Equal(1000, row.RequestBytes);
        Assert.Equal(600, row.ForwardedBytes);
        Assert.Equal(321, row.ResponseBytes);
        Assert.Equal(11, row.InputTokens);
        Assert.Equal(22, row.OutputTokens);
        Assert.Equal(33, row.CacheCreationInputTokens);
        Assert.Equal(44, row.CacheReadInputTokens);
        Assert.Null(row.UsageError);
        Assert.Equal("""{"body-read":1,"compression.reduce.Log":2500,"upstream.ttfb":1000000}""", row.Stages);
    }

    [Fact]
    public async Task A_response_without_usage_keeps_null_counts_and_the_reason()
    {
        await _store.StoreResponseAsync(
            new CapturedResponse("r1", DateTimeOffset.UnixEpoch, 529, 10, 10, 0, null, "over-cap", []),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await ReadResponsesAsync());
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.CacheCreationInputTokens);
        Assert.Null(row.CacheReadInputTokens);
        Assert.Equal("over-cap", row.UsageError);
        Assert.Equal("{}", row.Stages);
    }

    [Fact]
    public async Task A_capture_file_from_before_responses_were_kept_takes_responses_and_keeps_its_requests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await old.OpenAsync(TestContext.Current.CancellationToken);
            await old.ExecuteAsync("""
                CREATE TABLE capture_request (
                    request_id  TEXT PRIMARY KEY,
                    captured_at TEXT NOT NULL,
                    provider    TEXT NOT NULL,
                    host        TEXT NOT NULL,
                    path        TEXT NOT NULL,
                    body        BLOB NOT NULL
                );
                INSERT INTO capture_request VALUES ('old', '2026-10-22T00:00:00.0000000+00:00', 'Anthropic', 'api.anthropic.com', '/v1/messages', x'7B7D');
                """);
        }

        var store = new SqliteCaptureStore(_databasePath);
        await store.StoreAsync(Capture("new"), TestContext.Current.CancellationToken);
        await store.StoreResponseAsync(
            new CapturedResponse("new", DateTimeOffset.UnixEpoch, 200, 2, 2, 5, new ResponseUsage(1, 2, null, null), null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(["new", "old"], (await ReadRowsAsync()).Select(row => row.RequestId).Order(StringComparer.Ordinal));
        Assert.Equal("new", Assert.Single(await ReadResponsesAsync()).RequestId);
    }

    private async Task<IReadOnlyList<ResponseRow>> ReadResponsesAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<ResponseRow>("""
            SELECT request_id AS RequestId, completed_at AS CompletedAt, status AS Status,
                   request_bytes AS RequestBytes, forwarded_bytes AS ForwardedBytes, response_bytes AS ResponseBytes,
                   input_tokens AS InputTokens, output_tokens AS OutputTokens,
                   cache_creation_input_tokens AS CacheCreationInputTokens, cache_read_input_tokens AS CacheReadInputTokens,
                   usage_error AS UsageError, stages AS Stages
            FROM capture_response;
            """);
        return rows.ToList();
    }

    private sealed record ResponseRow(
        string RequestId,
        string CompletedAt,
        long Status,
        long RequestBytes,
        long ForwardedBytes,
        long ResponseBytes,
        long? InputTokens,
        long? OutputTokens,
        long? CacheCreationInputTokens,
        long? CacheReadInputTokens,
        string? UsageError,
        string Stages);

    private static CapturedRequest Capture(string requestId) =>
        new(requestId, DateTimeOffset.UnixEpoch, Dialect.Anthropic, "api.anthropic.com", "/v1/messages", "{}"u8.ToArray());

    private async Task<IReadOnlyList<CaptureRow>> ReadRowsAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<CaptureRow>("""
            SELECT request_id AS RequestId, captured_at AS CapturedAt, provider AS Provider,
                   host AS Host, path AS Path, body AS Body
            FROM capture_request;
            """);
        return rows.ToList();
    }

    private sealed record CaptureRow(string RequestId, string CapturedAt, string Provider, string Host, string Path, byte[] Body);
}
