using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>The tables the store keeps, and the connections it opens.</summary>
public static class ContentSchema
{
    /// <summary>The statements that make the database ready to use.</summary>
    public const string Statements = """
        CREATE TABLE IF NOT EXISTS content_request (
            request_id  TEXT PRIMARY KEY,
            created_at  TEXT NOT NULL,
            original    TEXT NOT NULL,
            compressed  TEXT NOT NULL,
            ratio       REAL NOT NULL,
            path        TEXT NOT NULL,
            chunk_count INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS content_chunk (
            request_id      TEXT NOT NULL,
            chunk_id        INTEGER NOT NULL,
            content_type    TEXT NOT NULL,
            original_text   TEXT NOT NULL,
            compressed_text TEXT NOT NULL,
            path            TEXT NOT NULL,
            ratio           REAL NOT NULL,
            PRIMARY KEY (request_id, chunk_id)
        );

        CREATE TABLE IF NOT EXISTS content_queue (
            job_id     INTEGER PRIMARY KEY AUTOINCREMENT,
            request_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            payload    TEXT NOT NULL
        );
        """;

    /// <summary>Opens a connection with the settings the store needs.</summary>
    /// <param name="path">The path of the database file.</param>
    public static SqliteConnection Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        Execute(connection, "PRAGMA busy_timeout = 5000;");
        Execute(connection, "PRAGMA journal_mode = WAL;");

        return connection;
    }

    /// <summary>Makes the tables, when they are not there yet.</summary>
    /// <param name="connection">The connection to prepare.</param>
    public static void Ensure(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Execute(connection, Statements);
    }

    /// <summary>Runs a statement that returns nothing.</summary>
    /// <param name="connection">The connection to run it on.</param>
    /// <param name="statement">The statement to run.</param>
    public static void Execute(SqliteConnection connection, string statement)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = statement;
        command.ExecuteNonQuery();
    }
}
