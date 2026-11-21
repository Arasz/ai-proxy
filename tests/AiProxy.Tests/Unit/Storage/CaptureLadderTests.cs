using AiProxy.Core.Capture;
using AiProxy.Core.Providers;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>capture.db opens through the schema ladder.</summary>
public sealed class CaptureLadderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ai-proxy-tests", Guid.CreateVersion7().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, SqliteCaptureStore.FileName);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private SqliteCaptureStore Store()
    {
        return new SqliteCaptureStore(DatabasePath, SqliteCaptureStore.DefaultMaxLiveBytes, TimeProvider.System, NullLogger<SqliteCaptureStore>.Instance);
    }

    private static CapturedRequest Request(string id)
    {
        return new CapturedRequest(id, DateTimeOffset.UtcNow, Dialect.Anthropic, "api.anthropic.com", "/v1/messages", new byte[10]);
    }

    private long UserVersion()
    {
        using var connection = ContentSchema.Open(DatabasePath);
        return connection.ExecuteScalar<long>("PRAGMA user_version;");
    }

    [Fact]
    public async Task A_version_0_file_from_the_last_release_moves_to_version_1_and_keeps_its_rows()
    {
        Directory.CreateDirectory(_directory);
        using (var legacy = ContentSchema.Open(DatabasePath))
        {
            legacy.Execute("""
                           CREATE TABLE capture_request (request_id TEXT PRIMARY KEY, captured_at TEXT NOT NULL, provider TEXT NOT NULL, host TEXT NOT NULL, path TEXT NOT NULL, body BLOB NOT NULL);
                           CREATE TABLE capture_response (request_id TEXT PRIMARY KEY, completed_at TEXT NOT NULL, status INTEGER, request_bytes INTEGER, forwarded_bytes INTEGER, response_bytes INTEGER,
                               input_tokens INTEGER, output_tokens INTEGER, cache_creation_input_tokens INTEGER, cache_read_input_tokens INTEGER, usage_error TEXT, stages TEXT NOT NULL);
                           INSERT INTO capture_request VALUES ('old', '2026-11-20T00:00:00.0000000+00:00', 'Anthropic', 'h', '/p', x'00');
                           """);
            Assert.Equal(0, legacy.ExecuteScalar<long>("PRAGMA user_version;"));
        }

        await Store().StoreAsync(Request("new"), TestContext.Current.CancellationToken);

        Assert.Equal(1, UserVersion());
        using var connection = ContentSchema.Open(DatabasePath);
        Assert.Equal(["new", "old"], connection.Query<string>("SELECT request_id FROM capture_request ORDER BY request_id;").ToList());
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('capture_response') WHERE name = 'response_message_id';"));
    }

    [Fact]
    public async Task A_file_with_a_higher_version_than_the_code_refuses_to_open_and_is_left_alone()
    {
        Directory.CreateDirectory(_directory);
        using (var future = ContentSchema.Open(DatabasePath))
        {
            future.Execute("CREATE TABLE capture_request (request_id TEXT); PRAGMA user_version = 99;");
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Store().StoreAsync(Request("r1"), TestContext.Current.CancellationToken));

        Assert.Contains("99", failure.Message, StringComparison.Ordinal);
        Assert.Contains(SqliteCaptureStore.FileName, failure.Message, StringComparison.Ordinal);
        Assert.Equal(99, UserVersion());
    }
}
