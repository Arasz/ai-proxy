using AiProxy.Core.Capture;
using AiProxy.Core.Providers;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>The schema versions of the content file, and the rule that every table has a retention decision.</summary>
public sealed class ContentSchemaTests : IDisposable
{
    /// <summary>The statements version 2 of the schema ran, kept here as the fixture of an old file.</summary>
    private const string VersionTwoStatements = """
        CREATE TABLE content_request (request_id TEXT PRIMARY KEY, created_at TEXT NOT NULL, original TEXT NOT NULL, compressed TEXT NOT NULL, ratio REAL NOT NULL, path TEXT NOT NULL, chunk_count INTEGER NOT NULL);
        CREATE TABLE content_chunk (request_id TEXT NOT NULL, chunk_id INTEGER NOT NULL, content_type TEXT NOT NULL, original_text TEXT NOT NULL, compressed_text TEXT NOT NULL, path TEXT NOT NULL, ratio REAL NOT NULL, PRIMARY KEY (request_id, chunk_id));
        CREATE TABLE content_queue (job_id INTEGER PRIMARY KEY AUTOINCREMENT, request_id TEXT NOT NULL, created_at TEXT NOT NULL, payload TEXT NOT NULL);
        CREATE TABLE ccr_original (id TEXT PRIMARY KEY, text TEXT NOT NULL, line_count INTEGER NOT NULL, created_at TEXT NOT NULL);
        CREATE INDEX ccr_original_created ON ccr_original (created_at);
        CREATE TABLE ccr_thread (message_id TEXT PRIMARY KEY, continue_from TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE INDEX ccr_thread_created ON ccr_thread (created_at);
        PRAGMA user_version = 2;
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ai-proxy-tests", Guid.CreateVersion7().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "content.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public void A_file_from_schema_version_two_gains_the_created_at_index_and_the_dead_letter_table()
    {
        Directory.CreateDirectory(_directory);
        using (var old = ContentSchema.Open(DatabasePath))
        {
            old.Execute(VersionTwoStatements);
        }

        using var connection = ContentSchema.Open(DatabasePath);
        connection.Ensure();

        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'content_request_created';"));
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'content_queue_dead';"));
        Assert.Equal(3L, connection.ExecuteScalar<long>("PRAGMA user_version;"));
    }

    [Fact]
    public void Ensuring_a_file_that_is_already_current_does_not_write()
    {
        Directory.CreateDirectory(_directory);
        using var observer = ContentSchema.Open(DatabasePath);
        observer.Ensure();
        var before = observer.ExecuteScalar<long>("PRAGMA data_version;");

        using (var other = ContentSchema.Open(DatabasePath))
        {
            other.Ensure();
        }

        Assert.Equal(before, observer.ExecuteScalar<long>("PRAGMA data_version;"));
    }

    [Fact]
    public async Task Every_table_in_the_content_and_capture_files_is_evicted_or_explicitly_exempt()
    {
        Directory.CreateDirectory(_directory);
        var capturePath = Path.Combine(_directory, SqliteCaptureStore.FileName);
        var time = new FakeTimeProvider();
        var capture = new SqliteCaptureStore(capturePath, SqliteCaptureStore.DefaultMaxLiveBytes, time, NullLogger<SqliteCaptureStore>.Instance);
        await capture.StoreAsync(new CapturedRequest("r1", time.GetUtcNow(), Dialect.Anthropic, "api.anthropic.com", "/v1/messages", new byte[] { 1 }), TestContext.Current.CancellationToken);
        await capture.StoreResponseAsync(new CapturedResponse("r1", time.GetUtcNow(), 200, 1, 1, 1, null, null, []), TestContext.Current.CancellationToken);

        using var content = ContentSchema.Open(DatabasePath);
        content.Ensure();

        var decided = RetentionRules.Content.Concat(RetentionRules.Capture)
            .SelectMany(rule => rule.ChildTable is null ? [rule.Table] : new[] { rule.Table, rule.ChildTable })
            .Concat(RetentionRules.Exempt)
            .ToHashSet(StringComparer.Ordinal);

        using var captureConnection = ContentSchema.Open(capturePath);
        var tables = content.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table';")
            .Concat(captureConnection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table';"))
            .ToList();

        Assert.NotEmpty(tables);
        Assert.All(tables, table => Assert.Contains(table, decided));
    }

    [Fact]
    public void A_new_content_file_is_readable_by_the_user_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_directory);

        using (var connection = ContentSchema.Open(DatabasePath))
        {
            connection.Ensure();
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(DatabasePath));
    }

    [Fact]
    public void An_existing_content_file_that_others_can_read_is_tightened_on_open()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        using (var connection = ContentSchema.Open(DatabasePath))
        {
            connection.Ensure();
        }

        File.SetUnixFileMode(DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        using (var again = ContentSchema.Open(DatabasePath))
        {
            again.Ensure();
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(DatabasePath));
    }
}
