using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>An ordered list of schema steps; step N moves a file from <c>user_version</c> N-1 to N.</summary>
/// <param name="steps">The steps in order. A new schema change is a new last step, never an edit of an old one.</param>
public sealed class SchemaLadder(IReadOnlyList<Action<SqliteConnection>> steps)
{
    /// <summary>The version a file has after every step has run.</summary>
    public int Version => steps.Count;

    /// <summary>Runs the steps above the file's version, each with its stamp in one immediate transaction.</summary>
    /// <param name="connection">The connection to the file.</param>
    /// <param name="fileName">The file's name, for the error of a file that is newer than the code.</param>
    /// <param name="commandTimeoutSeconds">How long the statements wait for the write lock.</param>
    /// <exception cref="InvalidOperationException">The file's version is higher than the newest step.</exception>
    public void Apply(SqliteConnection connection, string fileName, int commandTimeoutSeconds = 30)
    {
        var current = RequireNotNewer(ReadVersion(connection), fileName);
        if (current == Version)
        {
            return;
        }

        connection.Execute("BEGIN IMMEDIATE;", commandTimeoutSeconds);
        try
        {
            current = RequireNotNewer(ReadVersion(connection), fileName);
            for (var version = current + 1; version <= Version; version++)
            {
                steps[version - 1](connection);
                connection.Execute($"PRAGMA user_version = {version};", commandTimeoutSeconds);
            }

            connection.Execute("COMMIT;", commandTimeoutSeconds);
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    private int RequireNotNewer(long fileVersion, string fileName)
    {
        if (fileVersion > Version)
        {
            throw new InvalidOperationException($"{fileName} is at schema version {fileVersion}, but this build only knows versions up to {Version}; it was written by a newer ai-proxy.");
        }

        return (int)fileVersion;
    }

    private static long ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Rolls back the open transaction, when there is one.</summary>
    internal static void Rollback(SqliteConnection connection)
    {
        if (SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0)
        {
            connection.Execute("ROLLBACK;");
        }
    }
}
