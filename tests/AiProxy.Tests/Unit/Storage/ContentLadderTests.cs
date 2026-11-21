using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>content.db opens through the schema ladder.</summary>
public sealed class ContentLadderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ai-proxy-ladder-{Guid.CreateVersion7():N}", "content.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var directory = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return ContentSchema.Open(_path);
    }

    [Fact]
    public void A_file_with_every_table_and_no_stamp_opens_as_version_three_and_keeps_its_rows()
    {
        using (var first = Open())
        {
            first.Ensure();
            first.Execute("INSERT INTO ccr_original VALUES ('keep', 't', 1, '2026-10-25T00:00:00.0000000+00:00'); PRAGMA user_version = 0;");
        }

        using var connection = Open();
        connection.Ensure();

        Assert.Equal(3L, connection.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT count(*) FROM ccr_original;"));
    }

    [Fact]
    public void A_file_stamped_newer_than_the_code_refuses_to_open_and_is_left_alone()
    {
        using (var first = Open())
        {
            first.Ensure();
            first.Execute("PRAGMA user_version = 4;");
        }

        using var connection = Open();
        var error = Assert.Throws<InvalidOperationException>(() => connection.Ensure());

        Assert.Contains("content.db", error.Message, StringComparison.Ordinal);
        Assert.Equal(4L, connection.ExecuteScalar<long>("PRAGMA user_version;"));
    }
}
