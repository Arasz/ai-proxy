using System.Text;
using System.Text.Json;
using AiProxy.Core.Capture;
using AiProxy.Core.Timing;
using Dapper;

namespace AiProxy.Storage;

/// <summary>Keeps captured provider requests in their own SQLite file, readable by the user only.</summary>
/// <remarks>
/// The request table holds the body and where it was sent, and the response table holds only
/// numbers; neither has a column for headers, the query string or the response body, so a
/// credential or a model's answer has nowhere to land.
/// </remarks>
public sealed class SqliteCaptureStore : IRequestCaptureStore
{
    /// <summary>The file name of the capture database inside the proxy's data directory.</summary>
    public const string FileName = "capture.db";

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS capture_request (
            request_id  TEXT PRIMARY KEY,
            captured_at TEXT NOT NULL,
            provider    TEXT NOT NULL,
            host        TEXT NOT NULL,
            path        TEXT NOT NULL,
            body        BLOB NOT NULL
        );
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
        """;

    private const string Insert = """
        INSERT INTO capture_request (request_id, captured_at, provider, host, path, body)
        VALUES (@RequestId, @CapturedAt, @Provider, @Host, @Path, @Body);
        """;

    private const string InsertResponse = """
        INSERT INTO capture_response (
            request_id, completed_at, status, request_bytes, forwarded_bytes, response_bytes,
            input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, usage_error, stages)
        VALUES (
            @RequestId, @CompletedAt, @StatusCode, @RequestBytes, @ForwardedBytes, @ResponseBytes,
            @InputTokens, @OutputTokens, @CacheCreationInputTokens, @CacheReadInputTokens, @UsageError, @Stages);
        """;

    private const UnixFileMode UserOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _databasePath;
    private volatile bool _prepared;

    /// <summary>Holds the path of the file the captures go to; nothing is created until the first capture.</summary>
    public SqliteCaptureStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
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
        await connection.ExecuteAsync(new CommandDefinition(
            Insert,
            new
            {
                request.RequestId,
                CapturedAt = request.CapturedAt.ToString("O"),
                Provider = request.Provider.ToString(),
                request.Host,
                request.Path,
                Body = request.Body.ToArray(),
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
        await connection.ExecuteAsync(new CommandDefinition(
            InsertResponse,
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
                Stages = StagesJson(response.Stages),
            },
            commandTimeout: ContentSchema.RequestCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
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

    /// <summary>Creates the directory and the file user-only before SQLite writes a byte, then the tables.</summary>
    /// <remarks>SQLite gives the journal and WAL files the mode of the database file.</remarks>
    private void Prepare()
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
                UnixCreateMode = UserOnlyFile,
            }))
            {
            }

            File.SetUnixFileMode(_databasePath, UserOnlyFile);
        }

        using var connection = ContentSchema.Open(_databasePath);
        ContentSchema.Execute(connection, Schema, ContentSchema.RequestCommandTimeoutSeconds);
        _prepared = true;
    }
}
