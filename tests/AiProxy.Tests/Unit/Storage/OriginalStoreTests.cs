using AiProxy.Core.Content.Ccr;
using AiProxy.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>The CCR originals and the thread ledger, against a real database file.</summary>
public sealed class OriginalStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ai-proxy-originals-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteOriginalStore _store;

    public OriginalStoreTests()
    {
        _store = new SqliteOriginalStore(new ContentStoreOptions(_path), _time);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_kept_original_is_found_by_its_id_exactly_and_an_unknown_id_is_not()
    {
        const string text = "line 1\nzażółć \u0000 line 2\n";

        await _store.KeepAsync(new KeptOriginal("0123456789abcdef", text, 2), CancellationToken.None);

        Assert.Equal(text, await _store.FindAsync("0123456789abcdef", CancellationToken.None));
        Assert.Null(await _store.FindAsync("fedcba9876543210", CancellationToken.None));
    }

    [Fact]
    public async Task Keeping_the_same_id_twice_keeps_the_first()
    {
        await _store.KeepAsync(new KeptOriginal("0123456789abcdef", "first", 1), CancellationToken.None);
        await _store.KeepAsync(new KeptOriginal("0123456789abcdef", "second", 1), CancellationToken.None);

        Assert.Equal("first", await _store.FindAsync("0123456789abcdef", CancellationToken.None));
    }

    [Fact]
    public async Task The_ledger_says_which_message_a_continue_follows_and_knows_nothing_else()
    {
        await _store.LinkThreadAsync("msg_a", "msg_a", CancellationToken.None);
        await _store.LinkThreadAsync("msg_b", "msg_d", CancellationToken.None);

        Assert.Equal("msg_a", await _store.FindThreadAsync("msg_a", CancellationToken.None));
        Assert.Equal("msg_d", await _store.FindThreadAsync("msg_b", CancellationToken.None));
        Assert.Null(await _store.FindThreadAsync("msg_unknown", CancellationToken.None));
    }

    [Fact]
    public async Task Rows_older_than_fourteen_days_are_evicted_and_younger_rows_stay()
    {
        await _store.KeepAsync(new KeptOriginal("000000000000000a", "old", 1), CancellationToken.None);
        await _store.LinkThreadAsync("msg_old", "msg_old", CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(10));
        await _store.KeepAsync(new KeptOriginal("000000000000000b", "young", 1), CancellationToken.None);
        await _store.LinkThreadAsync("msg_young", "msg_young", CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(5));

        var evicted = await _store.EvictAsync(CancellationToken.None);

        Assert.Equal(2, evicted);
        Assert.Null(await _store.FindAsync("000000000000000a", CancellationToken.None));
        Assert.Null(await _store.FindThreadAsync("msg_old", CancellationToken.None));
        Assert.Equal("young", await _store.FindAsync("000000000000000b", CancellationToken.None));
        Assert.Equal("msg_young", await _store.FindThreadAsync("msg_young", CancellationToken.None));
    }

    [Fact]
    public async Task A_database_from_the_previous_schema_gets_the_new_tables_and_keeps_its_rows()
    {
        await using (var connection = ContentSchema.Open(_path))
        {
            connection.Execute("CREATE TABLE content_request (request_id TEXT PRIMARY KEY, created_at TEXT NOT NULL, original TEXT NOT NULL, compressed TEXT NOT NULL, ratio REAL NOT NULL, path TEXT NOT NULL, chunk_count INTEGER NOT NULL); INSERT INTO content_request VALUES ('r1','t','o','c',1,'p',0); PRAGMA user_version = 1;");
        }

        await _store.KeepAsync(new KeptOriginal("0123456789abcdef", "text", 1), CancellationToken.None);

        Assert.Equal("text", await _store.FindAsync("0123456789abcdef", CancellationToken.None));
        await using var check = ContentSchema.Open(_path);
        using var command = check.CreateCommand();
        command.CommandText = "SELECT count(*) FROM content_request;";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }
}
