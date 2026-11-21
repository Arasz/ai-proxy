using System.Runtime.CompilerServices;
using System.Text;
using AiProxy.Core.Replay;
using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>One stored request body, exactly as the database holds it.</summary>
/// <param name="Source">The database it came from.</param>
/// <param name="Id">The request id.</param>
/// <param name="Bytes">The stored bytes; a capture body may still be zstd.</param>
/// <param name="Provider">The provider a capture row names; null for a content row.</param>
/// <param name="Host">The host a capture row names; null for a content row.</param>
public sealed record StoredBody(ReplaySource Source, string Id, byte[] Bytes, string? Provider = null, string? Host = null);

/// <summary>Reads stored request bodies and the gain request count from the proxy's databases, which it opens read-only and never changes.</summary>
/// <param name="directory">The proxy data directory that holds <c>content.db</c>, <c>capture.db</c> and <c>gain.db</c>.</param>
public sealed class ReplayReader(string directory)
{
    private const string ContentFile = "content.db";
    private const string GainFile = "gain.db";

    /// <summary>Opens a database file read-only; any write through the connection throws.</summary>
    /// <param name="path">The database file; it must exist.</param>
    public static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Reads the bodies of one source, oldest first; a missing database yields none.</summary>
    /// <param name="source">The database to read.</param>
    /// <param name="since">The earliest request time to read, or null for all.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    public async IAsyncEnumerable<StoredBody> ReadAsync(ReplaySource source, DateTimeOffset? since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, source == ReplaySource.Content ? ContentFile : SqliteCaptureStore.FileName);
        if (!File.Exists(path))
        {
            yield break;
        }

        await using var connection = OpenReadOnly(path);
        await using var command = connection.CreateCommand();
        command.CommandText = source == ReplaySource.Content
            ? "SELECT request_id, original FROM content_request WHERE created_at >= @Since ORDER BY created_at"
            : "SELECT request_id, body, provider, host FROM capture_request WHERE captured_at >= @Since ORDER BY captured_at";
        command.Parameters.AddWithValue("@Since", (since ?? DateTimeOffset.MinValue).ToUniversalTime().ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return source == ReplaySource.Content
                ? new StoredBody(source, reader.GetString(0), Encoding.UTF8.GetBytes(reader.GetString(1)))
                : new StoredBody(source, reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetString(2), reader.GetString(3));
        }
    }

    /// <summary>Counts the request rows of the gain ledger from a time on; null when there is no ledger.</summary>
    /// <param name="since">The earliest time to count, or null for all.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    public async Task<long?> CountGainRequestsAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, GainFile);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var connection = OpenReadOnly(path);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM gain_event WHERE kind = 'request' AND at >= @Since";
        command.Parameters.AddWithValue("@Since", (since ?? DateTimeOffset.MinValue).UtcTicks);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}
