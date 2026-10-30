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
public sealed partial class SqliteCaptureStore : IRequestCaptureStore
{
    /// <summary>The file name of the capture database inside the proxy's data directory.</summary>
    public const string FileName = "capture.db";


    private const UnixFileMode UserOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The live bytes above which capture stops, when the operator sets none: 1 GB.</summary>
    public const long DefaultMaxLiveBytes = 1L << 30;

    private readonly string _databasePath;
    private readonly long _maxLiveBytes;
    private readonly TimeProvider _time;
    private readonly ILogger<SqliteCaptureStore> _logger;
    private readonly object _prepareGate = new();
    private volatile bool _prepared;
    private volatile bool _stopped;

    /// <summary>Holds the path of the file the captures go to; nothing is created until the first capture.</summary>
    /// <param name="databasePath">The path of the capture file.</param>
    /// <param name="maxLiveBytes">The live bytes of the file above which capture stops.</param>
    /// <param name="time">Decides which rows are old enough to evict.</param>
    /// <param name="logger">Where the stop and the resume of capture are logged.</param>
    public SqliteCaptureStore(string databasePath, long maxLiveBytes, TimeProvider time, ILogger<SqliteCaptureStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        _maxLiveBytes = maxLiveBytes;
        _time = time;
        _logger = logger;
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
        return await connection.EvictOlderThanAsync(RetentionRules.Capture, _time.GetUtcNow() - SqliteOriginalStore.Retention, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask StoreAsync(CapturedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_prepared)
        {
            Prepare();
        }

        await using var connection = ContentSchema.Open(_databasePath);
        if (await IsFullAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            Sql.Insert,
            new
            {
                request.RequestId,
                CapturedAt = request.CapturedAt.ToString("O"),
                Provider = request.Provider.ToString(),
                request.Host,
                request.Path,
                Body = request.Body.ToArray()
            },
            commandTimeout: ContentSchema.RequestCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask StoreResponseAsync(CapturedResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!_prepared)
        {
            Prepare();
        }

        await using var connection = ContentSchema.Open(_databasePath);
        if (await IsFullAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            Sql.InsertResponse,
            new
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
                Stages = StagesJson(response.Stages)
            },
            commandTimeout: ContentSchema.RequestCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Tells whether the live pages of the file are above the ceiling, and logs each change of answer once.</summary>
    /// <remarks>
    ///     The live size is the pages in use, not the size of the file: a delete moves pages to the free
    ///     list and never shrinks the file, so the file size would keep capture stopped after an eviction.
    /// </remarks>
    private async Task<bool> IsFullAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var pageSize = await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA page_size;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        var pages = await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA page_count;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        var free = await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA freelist_count;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        var liveBytes = (pages - free) * pageSize;
        var full = liveBytes > _maxLiveBytes;

        if (full && !_stopped)
        {
            _stopped = true;
            Log.Stopped(_logger, _databasePath, liveBytes, _maxLiveBytes);
        }
        else if (!full && _stopped)
        {
            _stopped = false;
            Log.Resumed(_logger, _databasePath, liveBytes);
        }

        return full;
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
        connection.Execute(Sql.Schema, ContentSchema.RequestCommandTimeoutSeconds);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1502, Level = LogLevel.Warning, Message = "Capture stopped: {Path} holds {LiveBytes} live bytes, above the ceiling of {MaxBytes}; it resumes when eviction frees pages")]
        public static partial void Stopped(ILogger logger, string path, long liveBytes, long maxBytes);

        [LoggerMessage(EventId = 1503, Level = LogLevel.Information, Message = "Capture resumed: {Path} holds {LiveBytes} live bytes")]
        public static partial void Resumed(ILogger logger, string path, long liveBytes);
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
                                         stages                      TEXT NOT NULL
                                     );
                                     CREATE INDEX IF NOT EXISTS capture_response_completed ON capture_response (completed_at);
                                     """;

        public const string Insert = """
                                     INSERT INTO capture_request (request_id, captured_at, provider, host, path, body)
                                     VALUES (@RequestId, @CapturedAt, @Provider, @Host, @Path, @Body);
                                     """;

        public const string InsertResponse = """
                                             INSERT INTO capture_response (
                                                 request_id, completed_at, status, request_bytes, forwarded_bytes, response_bytes,
                                                 input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, usage_error, stages)
                                             VALUES (
                                                 @RequestId, @CompletedAt, @StatusCode, @RequestBytes, @ForwardedBytes, @ResponseBytes,
                                                 @InputTokens, @OutputTokens, @CacheCreationInputTokens, @CacheReadInputTokens, @UsageError, @Stages);
                                             """;
    }
}