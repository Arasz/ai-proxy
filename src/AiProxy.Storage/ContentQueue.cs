using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiProxy.Core.Content;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>The queue is the worker drains.</summary>
public interface IContentQueue
{
    /// <summary>Reads the records waiting to be written.</summary>
    /// <param name="limit">How many to read at a time.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    Task<IReadOnlyList<ContentJob>> ReadAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Takes a job out of the queue, once it has been written.</summary>
    /// <param name="jobId">The queue row's number.</param>
    /// <param name="cancellationToken">The token that stops the deleted.</param>
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
///     The queue is the pub/sub channel. The request path writes a row and returns, and the
///     row is committed before the request is forwarded, so the promise that the original is
///     kept survives the process dying. The writer drains the queue on its own connection,
///     because the pragma that tells it whether anything changed does not move for a
///     connection's own commits.
/// </remarks>
public sealed class ContentQueue(ContentStoreOptions options, TimeProvider time) : IContentQueue
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _databasePath = options.ResolvedDatabasePath;

    /// <summary>Reads the records waiting to be written.</summary>
    /// <param name="limit">How many to read at a time.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    public async Task<IReadOnlyList<ContentJob>> ReadAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = ContentSchema.Open(_databasePath);
        connection.Ensure(ContentSchema.RequestCommandTimeoutSeconds);

        const string select = """
                              SELECT job_id AS JobId, request_id AS RequestId, created_at AS CreatedAt, payload AS Payload
                              FROM content_queue
                              ORDER BY job_id
                              LIMIT @Limit;
                              """;

        var getJobPayloadCommand = new CommandDefinition(select, new { Limit = limit }, cancellationToken: cancellationToken);
        var rows = (await connection.QueryAsync<JobPayload>(getJobPayloadCommand)).ToList();

        var jobs = new List<ContentJob>(rows.Count);
        foreach (var jobPayload in rows)
        {
            ContentRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<ContentRecord>(jobPayload.Payload, SerializerOptions);
            }
            catch (JsonException exception)
            {
                await DeadLetterAsync(connection, jobPayload, exception.Message, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (record is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM content_queue WHERE job_id = @JobId;",
                    new { jobPayload.JobId },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                continue;
            }

            jobs.Add(new ContentJob(jobPayload.JobId, record));
        }

        return jobs;
    }

    /// <summary>Takes a job out of the queue, once it has been written.</summary>
    /// <param name="jobId">The queue row's number.</param>
    /// <param name="cancellationToken">The token that stops the deleted.</param>
    /// <summary>Finds the record still waiting in the queue for a request, without taking it out.</summary>
    /// <param name="requestId">The request's id.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    public async Task<ContentRecord?> FindPendingAsync(string requestId, CancellationToken cancellationToken)
    {
        await using var connection = ContentSchema.Open(_databasePath);
        connection.Ensure(ContentSchema.RequestCommandTimeoutSeconds);

        var payload = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload FROM content_queue WHERE request_id = @RequestId ORDER BY job_id LIMIT 1;",
            new { RequestId = requestId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        try
        {
            return payload is null ? null : JsonSerializer.Deserialize<ContentRecord>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA data_version;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Publishes a record and returns once the row is committed.</summary>
    /// <param name="record">The record to publish.</param>
    /// <param name="cancellationToken">The token that stops the writer.</param>
    public async ValueTask PublishAsync(ContentRecord record, CancellationToken cancellationToken)
    {

        await using var connection = ContentSchema.Open(_databasePath);
        connection.Ensure(ContentSchema.RequestCommandTimeoutSeconds);

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
                Payload = JsonSerializer.Serialize(record, SerializerOptions)
            },
            commandTimeout: ContentSchema.RequestCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Moves a row that cannot be read out of the queue, keeping its payload and why it failed.</summary>
    private async Task DeadLetterAsync(SqliteConnection connection, JobPayload job, string error, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR REPLACE INTO content_queue_dead (job_id, request_id, created_at, failed_at, payload, error)
            VALUES (@JobId, @RequestId, @CreatedAt, @FailedAt, @Payload, @Error);
            """,
            new { job.JobId, job.RequestId, job.CreatedAt, FailedAt = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), job.Payload, Error = error },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM content_queue WHERE job_id = @JobId;",
            new { job.JobId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record JobPayload(long JobId, string RequestId, string CreatedAt, string Payload);
}