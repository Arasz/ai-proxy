using AiProxy.Core.Capture;
using AiProxy.Core.Providers;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>The capture store, tested against a real database file.</summary>
public class CaptureStoreTests : IDisposable
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
