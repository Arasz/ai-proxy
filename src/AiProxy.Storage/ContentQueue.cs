using System.Text.Json;
using System.Text.Json.Serialization;
using AiProxy.Core.Content;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>The queue the worker drains.</summary>
public interface IContentQueue
{
    /// <summary>Reads the records waiting to be written.</summary>
    /// <param name="limit">How many to read at a time.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    Task<IReadOnlyList<ContentJob>> ReadAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Takes a job out of the queue, once it has been written.</summary>
    /// <param name="jobId">The queue row's number.</param>
    /// <param name="cancellationToken">The token that stops the delete.</param>
    ValueTask RemoveAsync(long jobId, CancellationToken cancellationToken);

    /// <summary>The value of the pragma that tells whether another connection committed.</summary>
    /// <param name="connection">The connection to read it on.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    Task<long> ReadDataVersionAsync(SqliteConnection connection, CancellationToken cancellationToken);
}

/// <summary>A record waiting to be written to the content tables.</summary>
/// <param name="JobId">The queue row's number.</param>
/// <param name="Record">The record the row carries.</param>
public sealed record ContentJob(long JobId, ContentRecord Record);

/// <summary>The queue that carries a record from the request path to the writer.</summary>
/// <remarks>
/// The queue is the pub/sub channel. The request path writes a row and returns, and the
/// row is committed before the request is forwarded, so the promise that the original is
/// kept survives the process dying. The writer drains the queue on its own connection,
/// because the pragma that tells it whether anything changed does not move for a
/// connection's own commits.
/// </remarks>
public sealed class ContentQueue : IContentQueue
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _databasePath;

    /// <summary>Holds the path of the database the queue lives in.</summary>
    /// <param name="options">Where the store keeps what the proxy compressed.</param>
    public ContentQueue(ContentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _databasePath = options.ResolvedDatabasePath;
    }

    /// <summary>Publishes a record, and returns once the row is committed.</summary>
    /// <param name="record">The record to publish.</param>
    /// <param name="cancellationToken">The token that stops the write.</param>
    public async ValueTask PublishAsync(ContentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = ContentSchema.Open(_databasePath);
        ContentSchema.Ensure(connection, ContentSchema.RequestCommandTimeoutSeconds);

        const string insert = """
            INSERT INTO content_queue (request_id, created_at, payload)
            VALUES (@RequestId, @CreatedAt, @Payload);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            insert,
            new
            {
                record.RequestId,
                CreatedAt = record.CreatedAt.ToString("O"),
                Payload = JsonSerializer.Serialize(record, SerializerOptions),
            },
            commandTimeout: ContentSchema.RequestCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Reads the records waiting to be written.</summary>
    /// <param name="limit">How many to read at a time.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    public async Task<IReadOnlyList<ContentJob>> ReadAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = ContentSchema.Open(_databasePath);
        ContentSchema.Ensure(connection, ContentSchema.RequestCommandTimeoutSeconds);

        const string select = """
            SELECT job_id AS JobId, payload AS Payload
            FROM content_queue
            ORDER BY job_id
            LIMIT @Limit;
            """;

        var rows = await connection.QueryAsync<(long JobId, string Payload)>(
            new CommandDefinition(select, new { Limit = limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var jobs = new List<ContentJob>(rows.Count());
        foreach (var row in rows)
        {
            var record = JsonSerializer.Deserialize<ContentRecord>(row.Payload, SerializerOptions);
            if (record is not null)
            {
                jobs.Add(new ContentJob(row.JobId, record));
            }
        }

        return jobs;
    }

    /// <summary>Takes a job out of the queue, once it has been written.</summary>
    /// <param name="jobId">The queue row's number.</param>
    /// <param name="cancellationToken">The token that stops the delete.</param>
    public async ValueTask RemoveAsync(long jobId, CancellationToken cancellationToken)
    {
        await using var connection = ContentSchema.Open(_databasePath);

        const string delete = "DELETE FROM content_queue WHERE job_id = @JobId;";

        await connection.ExecuteAsync(new CommandDefinition(
            delete,
            new { JobId = jobId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The value of the pragma that tells whether another connection committed.</summary>
    /// <param name="connection">The connection to read it on.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    public async Task<long> ReadDataVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA data_version;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
