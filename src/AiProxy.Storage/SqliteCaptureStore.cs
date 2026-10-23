using AiProxy.Core.Capture;
using Dapper;

namespace AiProxy.Storage;

/// <summary>Keeps captured provider requests in their own SQLite file, readable by the user only.</summary>
/// <remarks>
/// The table holds the body and where it was sent, and has no column for headers or the query
/// string, so a credential the client sent with the request has nowhere to land.
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
        """;

    private const string Insert = """
        INSERT INTO capture_request (request_id, captured_at, provider, host, path, body)
        VALUES (@RequestId, @CapturedAt, @Provider, @Host, @Path, @Body);
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

    /// <summary>Creates the directory and the file user-only before SQLite writes a byte, then the table.</summary>
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
