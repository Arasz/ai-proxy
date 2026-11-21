using System.Globalization;
using System.Text;
using System.Text.Json;
using AiProxy.Core.Capture;
using AiProxy.Core.Timing;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiProxy.Storage;

/// <summary>Keeps captured provider requests in their own SQLite file, readable by the user only.</summary>
/// <remarks>
///     The request table holds the body and where it was sent, and the response table holds only
///     numbers; neither has a column for headers, the query string or the response body, so a
///     credential or a model's answer has nowhere to land.
/// </remarks>
/// <param name="databasePath">The path of the capture file; nothing is created until the first capture.</param>
/// <param name="maxLiveBytes">The live bytes the file may hold; a write that would pass it first evicts the oldest exchanges.</param>
/// <param name="time">Decides which rows are old enough to evict.</param>
/// <param name="logger">Where evictions and rejected writes are logged.</param>
public sealed partial class SqliteCaptureStore(string databasePath, long maxLiveBytes, TimeProvider time, ILogger<SqliteCaptureStore> logger) : IRequestCaptureStore
{
    /// <summary>The file name of the capture database inside the proxy's data directory.</summary>
    public const string FileName = "capture.db";

    private const UnixFileMode UserOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The live bytes the file may hold, when the operator sets none: 1 GB.</summary>
    public const long DefaultMaxLiveBytes = 1L << 30;

    /// <summary>What a row costs beyond the bytes of its values, so the estimate of a write errs high.</summary>
    private const int RowOverheadBytes = 256;

    /// <summary>The most exchanges one eviction pass looks at.</summary>
    private const int EvictionBatch = 64;

    private static readonly SchemaLadder Ladder = new([CreateTables]);

    private readonly string _databasePath = RequirePath(databasePath);
    private readonly object _prepareGate = new();
    private volatile bool _prepared;
    private long _lastEvictionTicks;
    private long _lastWriteErrorTicks;
    private volatile string? _lastWriteError;

    private static string RequirePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    /// <summary>Deletes the captured requests and responses older than <see cref="SqliteOriginalStore.Retention" />, and says how many went.</summary>
    /// <remarks>A file that was never written is left alone, and not created.</remarks>
    /// <param name="cancellationToken">The token that stops the pass between batches.</param>
    public async Task<int> EvictAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return 0;
        }

        if (!_prepared)
        {
            Prepare();
        }

        await using var connection = ContentSchema.Open(_databasePath);
        var removed = await connection.EvictOlderThanAsync(RetentionRules.Capture, time.GetUtcNow() - SqliteOriginalStore.Retention, cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            Interlocked.Exchange(ref _lastEvictionTicks, time.GetUtcNow().UtcTicks);
        }

        return removed;
    }

    /// <summary>The oldest requests by capture time, read through the time index; takes <c>@Keep</c> and <c>@Batch</c>.</summary>
    public const string OldestRequestsQuery = Sql.OldestExchange;

    /// <summary>Reads the live size, the cap, the oldest row and the last eviction and write error.</summary>
    /// <remarks>It never creates, migrates or writes the file. A file that was never written reports an empty state, and a file newer than the code reports that as the error.</remarks>
    /// <param name="cancellationToken">The token that stops the read.</param>
    public async Task<CaptureState> GetStateAsync(CancellationToken cancellationToken)
    {
        var lastEviction = Interlocked.Read(ref _lastEvictionTicks);
        DateTimeOffset? lastEvictionAt = lastEviction == 0 ? null : new DateTimeOffset(lastEviction, TimeSpan.Zero);
        var errorTicks = Interlocked.Read(ref _lastWriteErrorTicks);
        DateTimeOffset? errorAt = errorTicks == 0 ? null : new DateTimeOffset(errorTicks, TimeSpan.Zero);
        var error = _lastWriteError;
        if (!File.Exists(_databasePath))
        {
            return new CaptureState(0, maxLiveBytes, null, lastEvictionAt, error, errorAt);
        }

        try
        {
            await using var connection = ContentSchema.Open(_databasePath);
            var live = await LivePagesAsync(connection, cancellationToken).ConfigureAwait(false) * await PageSizeAsync(connection, cancellationToken).ConfigureAwait(false);
            var version = await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA user_version;", cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (version > Ladder.Version)
            {
                return new CaptureState(live, maxLiveBytes, null, lastEvictionAt, $"{FileName} is at schema version {version}, but this build only knows versions up to {Ladder.Version}; it was written by a newer ai-proxy.", time.GetUtcNow());
            }

            var oldest = version == 0
                ? null
                : await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT MIN(captured_at) FROM capture_request;", cancellationToken: cancellationToken)).ConfigureAwait(false);
            DateTimeOffset? oldestAt = oldest is null ? null : DateTimeOffset.Parse(oldest, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return new CaptureState(live, maxLiveBytes, oldestAt, lastEvictionAt, error, errorAt);
        }
        catch (SqliteException exception)
        {
            return new CaptureState(0, maxLiveBytes, null, lastEvictionAt, exception.Message, time.GetUtcNow());
        }
    }

    /// <inheritdoc />
    public async ValueTask StoreAsync(CapturedRequest request, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            request.RequestId,
            CapturedAt = request.CapturedAt.ToString("O"),
            Provider = request.Provider.ToString(),
            request.Host,
            request.Path,
            Body = request.Body.ToArray()
        };

        var payloadBytes = parameters.Body.Length + request.Host.Length + request.Path.Length + RowOverheadBytes;
        await WriteAsync(payloadBytes, null, Sql.Insert, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask StoreResponseAsync(CapturedResponse response, CancellationToken cancellationToken)
    {
        var stages = StagesJson(response.Stages);
        var parameters = new
        {
            response.RequestId,
            CompletedAt = response.CompletedAt.ToString("O"),
            response.StatusCode,
            response.RequestBytes,
            response.ForwardedBytes,
            response.ResponseBytes,
            response.Usage?.InputTokens,
            response.Usage?.OutputTokens,
            response.Usage?.CacheCreationInputTokens,
            response.Usage?.CacheReadInputTokens,
            response.UsageError,
            response.MessageId,
            Stages = stages
        };

        var payloadBytes = stages.Length + (response.UsageError?.Length ?? 0) + (response.MessageId?.Length ?? 0) + RowOverheadBytes;
        await WriteAsync(payloadBytes, response.RequestId, Sql.InsertResponse, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts one row after evicting the oldest exchanges until the live pages and the row fit the cap, all in one transaction.</summary>
    /// <remarks>
    ///     A write bigger than the cap, or one that cannot fit after every other exchange is gone, is
    ///     rolled back and recorded as the last write error. A response whose request row is gone is dropped.
    /// </remarks>
    /// <param name="payloadBytes">The estimated bytes the row adds.</param>
    /// <param name="responseFor">The request id of a response write, which keeps its own exchange from being evicted; null for a request.</param>
    /// <param name="insertSql">The insert statement.</param>
    /// <param name="parameters">The statement's parameters.</param>
    /// <param name="cancellationToken">The token that stops the write.</param>
    private async ValueTask WriteAsync(long payloadBytes, string? responseFor, string insertSql, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            if (!_prepared)
            {
                Prepare();
            }

            await using var connection = ContentSchema.Open(_databasePath);
            var pageSize = await PageSizeAsync(connection, cancellationToken).ConfigureAwait(false);
            var capPages = maxLiveBytes / pageSize;
            var needPages = PagesFor(payloadBytes, pageSize);
            if (needPages > capPages)
            {
                Reject(payloadBytes);
                return;
            }

            await BeginAsync(connection, cancellationToken).ConfigureAwait(false);
            try
            {
                if (responseFor is not null && !await RequestRowExistsAsync(connection, responseFor, cancellationToken).ConfigureAwait(false))
                {
                    SchemaLadder.Rollback(connection);
                    Log.ResponseDropped(logger, responseFor);
                    return;
                }

                var evicted = await MakeRoomAsync(connection, capPages, needPages, responseFor ?? string.Empty, cancellationToken).ConfigureAwait(false);
                if (evicted < 0)
                {
                    SchemaLadder.Rollback(connection);
                    Reject(payloadBytes);
                    return;
                }

                await connection.ExecuteAsync(new CommandDefinition(insertSql, parameters, commandTimeout: ContentSchema.RequestCommandTimeoutSeconds, cancellationToken: cancellationToken)).ConfigureAwait(false);
                await connection.ExecuteAsync(new CommandDefinition("COMMIT;", commandTimeout: ContentSchema.RequestCommandTimeoutSeconds, cancellationToken: cancellationToken)).ConfigureAwait(false);

                if (evicted > 0)
                {
                    Interlocked.Exchange(ref _lastEvictionTicks, time.GetUtcNow().UtcTicks);
                    Log.CapEvicted(logger, evicted, _databasePath, maxLiveBytes);
                }
            }
            catch
            {
                SchemaLadder.Rollback(connection);
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordError(exception.Message);
            throw;
        }
    }

    private void RecordError(string message)
    {
        _lastWriteError = message;
        Interlocked.Exchange(ref _lastWriteErrorTicks, time.GetUtcNow().UtcTicks);
    }

    private void Reject(long payloadBytes)
    {
        RecordError($"a write of about {payloadBytes} bytes does not fit the cap of {maxLiveBytes} bytes and was not stored");
        Log.WriteRejected(logger, _databasePath, payloadBytes, maxLiveBytes);
    }

    /// <summary>Deletes the oldest whole exchanges, in batches sized to the missing pages, until the live pages plus the write fit; returns how many went, or -1 when they cannot fit.</summary>
    private static async Task<int> MakeRoomAsync(SqliteConnection connection, long capPages, long needPages, string keepRequestId, CancellationToken cancellationToken)
    {
        var pageSize = await PageSizeAsync(connection, cancellationToken).ConfigureAwait(false);
        var evicted = 0;
        while (true)
        {
            var missing = await LivePagesAsync(connection, cancellationToken).ConfigureAwait(false) + needPages - capPages;
            if (missing <= 0)
            {
                return evicted;
            }

            var oldest = (await connection.QueryAsync<OldRow>(new CommandDefinition(
                OldestRequestsQuery,
                new { Keep = keepRequestId, Batch = EvictionBatch },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
            var ids = new List<string>();
            var covered = 0L;
            foreach (var row in oldest)
            {
                ids.Add(row.Id);
                covered += PagesFor(row.Bytes, pageSize);
                if (covered >= missing)
                {
                    break;
                }
            }

            if (ids.Count == 0)
            {
                ids = (await connection.QueryAsync<string>(new CommandDefinition(
                    Sql.OldestOrphanResponses,
                    new { Keep = keepRequestId },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
                if (ids.Count == 0)
                {
                    return -1;
                }
            }

            await connection.ExecuteAsync(new CommandDefinition(Sql.DeleteExchanges, new { Ids = ids }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            evicted += ids.Count;
        }
    }

    private sealed class OldRow
    {
        public string Id { get; init; } = string.Empty;

        public long Bytes { get; init; }
    }

    private static async Task<bool> RequestRowExistsAsync(SqliteConnection connection, string requestId, CancellationToken cancellationToken)
    {
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM capture_request WHERE request_id = @requestId;",
            new { requestId },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    private static Task BeginAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE;", commandTimeout: ContentSchema.RequestCommandTimeoutSeconds, cancellationToken: cancellationToken));
    }

    /// <summary>The pages a row of the estimated size takes, with room for its index entries and overflow links.</summary>
    private static long PagesFor(long payloadBytes, long pageSize)
    {
        var pages = (payloadBytes + pageSize - 1) / pageSize;
        return pages + (pages / 32) + 2;
    }

    private static async Task<long> PageSizeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA page_size;", cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The pages in use, not the size of the file: a delete moves pages to the free list and never shrinks the file.</summary>
    private static async Task<long> LivePagesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var pages = await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA page_count;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        var free = await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA freelist_count;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return pages - free;
    }

    /// <summary>The stages as one JSON object of whole microseconds, in the order they ran.</summary>
    private static string StagesJson(IReadOnlyList<StageTiming> stages)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var stage in stages)
            {
                writer.WriteNumber(stage.Name, (long)stage.Elapsed.TotalMicroseconds);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Makes the file and its tables once, whichever of the writer and the eviction pass gets here first.</summary>
    private void Prepare()
    {
        lock (_prepareGate)
        {
            if (_prepared)
            {
                return;
            }

            CreateFileAndSchema();
            _prepared = true;
        }
    }

    /// <summary>Creates the directory and the file user-only before SQLite writes a byte, then the tables.</summary>
    /// <remarks>SQLite gives the journal and WAL files the mode of the database file.</remarks>
    private void CreateFileAndSchema()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath))!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UserOnlyDirectory);
            File.SetUnixFileMode(directory, UserOnlyDirectory);
            using (new FileStream(_databasePath, new FileStreamOptions
                   {
                       Mode = FileMode.OpenOrCreate,
                       Access = FileAccess.ReadWrite,
                       UnixCreateMode = UserOnlyFile
                   }))
            {
            }

            File.SetUnixFileMode(_databasePath, UserOnlyFile);
        }

        using var connection = ContentSchema.Open(_databasePath);
        Ladder.Apply(connection, FileName, ContentSchema.RequestCommandTimeoutSeconds);
    }

    /// <summary>Step 1: the tables of the 0.34.0 file, and the response identifier column that file may lack.</summary>
    private static void CreateTables(SqliteConnection connection)
    {
        connection.Execute(Sql.Schema, ContentSchema.RequestCommandTimeoutSeconds);
        var hasMessageId = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('capture_response') WHERE name = 'response_message_id';") > 0;
        if (!hasMessageId)
        {
            connection.Execute("ALTER TABLE capture_response ADD COLUMN response_message_id TEXT;", ContentSchema.RequestCommandTimeoutSeconds);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1504, Level = LogLevel.Information, Message = "Capture evicted {Exchanges} oldest exchanges from {Path} to stay within {MaxBytes} live bytes")]
        public static partial void CapEvicted(ILogger logger, int exchanges, string path, long maxBytes);

        [LoggerMessage(EventId = 1505, Level = LogLevel.Warning, Message = "Capture rejected a write of about {PayloadBytes} bytes to {Path}: it does not fit the cap of {MaxBytes} live bytes")]
        public static partial void WriteRejected(ILogger logger, string path, long payloadBytes, long maxBytes);

        [LoggerMessage(EventId = 1506, Level = LogLevel.Debug, Message = "Capture dropped the response of {RequestId}: its request row is not in the file")]
        public static partial void ResponseDropped(ILogger logger, string requestId);
    }

    private static class Sql
    {
        public const string Schema = """
                                     CREATE TABLE IF NOT EXISTS capture_request (
                                         request_id  TEXT PRIMARY KEY,
                                         captured_at TEXT NOT NULL,
                                         provider    TEXT NOT NULL,
                                         host        TEXT NOT NULL,
                                         path        TEXT NOT NULL,
                                         body        BLOB NOT NULL
                                     );
                                     CREATE INDEX IF NOT EXISTS capture_request_captured ON capture_request (captured_at);
                                     CREATE TABLE IF NOT EXISTS capture_response (
                                         request_id                  TEXT PRIMARY KEY,
                                         completed_at                TEXT NOT NULL,
                                         status                      INTEGER,
                                         request_bytes               INTEGER,
                                         forwarded_bytes             INTEGER,
                                         response_bytes              INTEGER,
                                         input_tokens                INTEGER,
                                         output_tokens               INTEGER,
                                         cache_creation_input_tokens INTEGER,
                                         cache_read_input_tokens     INTEGER,
                                         usage_error                 TEXT,
                                         response_message_id        TEXT,
                                         stages                      TEXT NOT NULL
                                     );
                                     CREATE INDEX IF NOT EXISTS capture_response_completed ON capture_response (completed_at);
                                     """;

        public const string OldestExchange = """
                                             SELECT request_id AS Id, CAST(length(body) AS INTEGER) AS Bytes FROM capture_request
                                             WHERE captured_at >= '' AND request_id <> @Keep
                                             ORDER BY captured_at LIMIT @Batch;
                                             """;

        public const string OldestOrphanResponses = """
                                                    SELECT request_id FROM capture_response
                                                    WHERE completed_at >= '' AND request_id <> @Keep
                                                    ORDER BY completed_at LIMIT 64;
                                                    """;

        public const string DeleteExchanges = """
                                              DELETE FROM capture_response WHERE request_id IN @Ids;
                                              DELETE FROM capture_request WHERE request_id IN @Ids;
                                              """;

        public const string Insert = """
                                     INSERT INTO capture_request (request_id, captured_at, provider, host, path, body)
                                     VALUES (@RequestId, @CapturedAt, @Provider, @Host, @Path, @Body);
                                     """;

        public const string InsertResponse = """
                                             INSERT INTO capture_response (
                                                 request_id, completed_at, status, request_bytes, forwarded_bytes, response_bytes,
                                                 input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, usage_error, response_message_id, stages)
                                             VALUES (
                                                 @RequestId, @CompletedAt, @StatusCode, @RequestBytes, @ForwardedBytes, @ResponseBytes,
                                                 @InputTokens, @OutputTokens, @CacheCreationInputTokens, @CacheReadInputTokens, @UsageError, @MessageId, @Stages);
                                             """;
    }
}
