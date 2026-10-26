using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>The tables the store keeps, and the connections it opens.</summary>
public static class ContentSchema
{
    /// <summary>How long a command waits for the write lock, in seconds.</summary>
    private const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>How long the request path waits for the write lock, in seconds.</summary>
    /// <remarks>
    ///     The producer's write stands between a request and its provider, so it gives up
    ///     quickly and lets the request go out without the record. The worker, which runs
    ///     with nothing in front of it, waits the long way and retries.
    /// </remarks>
    public const int RequestCommandTimeoutSeconds = 1;

    /// <summary>The version of the schema this build writes.</summary>
    private const int SchemaVersion = 2;

    /// <summary>Opens a connection with the settings the store needs.</summary>
    /// <param name="path">The path of the database file.</param>
    /// <remarks>
    ///     There is no busy timeout pragma here on purpose. The managed stack sets the
    ///     busy handler from the timeout of the command that runs, so a pragma on the
    ///     connection is overridden by every command and only says what it does not do.
    ///     The wait is bounded by the command timeout instead, and the two paths that
    ///     write set it to the bound they need.
    ///     The journal mode is a property of the file, so it is set when the file is not
    ///     in it yet and left alone after that. Setting it again is a write, and a write
    ///     moves the change counter that the worker's poll reads.
    /// </remarks>
    public static SqliteConnection Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = Convert.ToString(command.ExecuteScalar());
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase)) connection.Execute("PRAGMA journal_mode = WAL;");

        return connection;
    }

    /// <param name="connection">The connection to prepare.</param>
    extension(SqliteConnection connection)
    {
        /// <summary>Makes the tables when they are not there yet.</summary>
        /// <remarks>
        ///     The check is a read of the file's user version, and not a run of the create
        ///     statements on every connection. The create statements take a write lock, and a
        ///     write moves the change counter that the worker's poll reads, so running them on
        ///     every read would make the poll read every time and defeat the gate.
        /// </remarks>
        /// <param name="commandTimeoutSeconds">How long the statements wait for the write lock.</param>
        public void Ensure(int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
        {
            ArgumentNullException.ThrowIfNull(connection);

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt64(command.ExecuteScalar());
            if (version >= SchemaVersion) return;

            connection.Execute(Sql.Statements, commandTimeoutSeconds);
            connection.Execute($"PRAGMA user_version = {SchemaVersion};", commandTimeoutSeconds);
        }

        /// <summary>Runs a statement that returns nothing.</summary>
        /// <param name="statement">The statement to run.</param>
        /// <param name="commandTimeoutSeconds">How long the statement waits for the write lock.</param>
        public void Execute(string statement, int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.CommandTimeout = commandTimeoutSeconds;
            command.ExecuteNonQuery();
        }
    }

    private static class Sql
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

                                         CREATE TABLE IF NOT EXISTS ccr_original (
                                             id         TEXT PRIMARY KEY,
                                             text       TEXT NOT NULL,
                                             line_count INTEGER NOT NULL,
                                             created_at TEXT NOT NULL
                                         );

                                         CREATE INDEX IF NOT EXISTS ccr_original_created ON ccr_original (created_at);

                                         CREATE TABLE IF NOT EXISTS ccr_thread (
                                             message_id    TEXT PRIMARY KEY,
                                             continue_from TEXT NOT NULL,
                                             created_at    TEXT NOT NULL
                                         );

                                         CREATE INDEX IF NOT EXISTS ccr_thread_created ON ccr_thread (created_at);
                                         """;
    }
}