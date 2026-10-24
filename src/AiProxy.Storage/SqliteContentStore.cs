using AiProxy.Core.Content;
using Dapper;

namespace AiProxy.Storage;

/// <summary>Keeps what the proxy compressed, in a SQLite file.</summary>
/// <remarks>
/// The write is a hand-off, not a write: the record is published to the queue and the
/// background writer puts it in the content tables. The read looks in the content tables
/// first and in the queue second, so a record that has not been written yet is still
/// served.
/// </remarks>
public sealed class SqliteContentStore : IContentStore, IContentWriter
{
    private readonly ContentQueue _queue;
    private readonly string _databasePath;

    /// <summary>Holds the queue the record is published to, and the file it lives in.</summary>
    /// <param name="options">Where the store keeps what the proxy compressed.</param>
    /// <param name="queue">The queue that carries the record to the writer.</param>
    public SqliteContentStore(ContentStoreOptions options, ContentQueue queue)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);

        _databasePath = options.ResolvedDatabasePath;
        _queue = queue;
    }

    /// <inheritdoc />
    public ValueTask StoreAsync(ContentRecord record, CancellationToken cancellationToken) =>
        _queue.PublishAsync(record, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ContentRecord?> GetAsync(string requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestId);

        await using var connection = ContentSchema.Open(_databasePath);
        connection.Ensure(ContentSchema.RequestCommandTimeoutSeconds);

        const string select = """
            SELECT request_id      AS RequestId,
                   created_at      AS CreatedAt,
                   original        AS Original,
                   compressed      AS Compressed,
                   ratio           AS Ratio,
                   path            AS Path,
                   chunk_count     AS ChunkCount
            FROM content_request
            WHERE request_id = @RequestId;
            """;

        var row = await connection.QuerySingleOrDefaultAsync<ContentRequestRow>(new CommandDefinition(
            select,
            new { RequestId = requestId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            var pending = (await _queue.ReadAsync(64, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(job => string.Equals(job.Record.RequestId, requestId, StringComparison.Ordinal));

            return pending?.Record;
        }

        const string selectChunks = """
            SELECT chunk_id        AS ChunkId,
                   content_type    AS ContentType,
                   original_text   AS OriginalText,
                   compressed_text AS CompressedText,
                   path            AS Path,
                   ratio           AS Ratio
            FROM content_chunk
            WHERE request_id = @RequestId
            ORDER BY chunk_id;
            """;

        var chunks = await connection.QueryAsync<ContentChunkRow>(new CommandDefinition(
            selectChunks,
            new { RequestId = requestId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var parts = chunks
            .Select(chunk => new CompressedPart(
                (int)chunk.ChunkId,
                Enum.Parse<ContentType>(chunk.ContentType, ignoreCase: true),
                chunk.CompressedText,
                chunk.OriginalText,
                chunk.Path,
                chunk.Ratio))
            .ToList();

        return new ContentRecord(
            row.RequestId,
            DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture),
            row.Original,
            row.Compressed,
            row.Ratio,
            row.Path,
            (int)row.ChunkCount,
            parts);
    }

    /// <inheritdoc />
    public async ValueTask PersistAsync(ContentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = ContentSchema.Open(_databasePath);
        connection.Ensure();

        const string insertRequest = """
            INSERT OR REPLACE INTO content_request
                (request_id, created_at, original, compressed, ratio, path, chunk_count)
            VALUES
                (@RequestId, @CreatedAt, @Original, @Compressed, @Ratio, @Path, @ChunkCount);
            """;

        const string insertChunk = """
            INSERT OR REPLACE INTO content_chunk
                (request_id, chunk_id, content_type, original_text, compressed_text, path, ratio)
            VALUES
                (@RequestId, @ChunkId, @ContentType, @OriginalText, @CompressedText, @Path, @Ratio);
            """;

        // The request row and its chunks are one fact, so they are written together or
        // not at all. A reader that found a request row without its chunks would serve
        // a record that says how many parts there were and shows none of them.
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            insertRequest,
            new
            {
                record.RequestId,
                CreatedAt = record.CreatedAt.ToString("O"),
                record.Original,
                record.Compressed,
                record.Ratio,
                record.Path,
                record.ChunkCount,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var part in record.Parts)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                insertChunk,
                new
                {
                    record.RequestId,
                    part.ChunkId,
                    ContentType = part.Type.ToString(),
                    part.OriginalText,
                    CompressedText = part.Text,
                    part.Path,
                    part.Ratio,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
