using System.Text.Json;
using AiProxy.Core.Gain;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>The metadata ledger's private database path.</summary>
public sealed record GainStoreOptions(string DatabasePath);

/// <summary>Persists content-free statistics separately from originals and optional captures.</summary>
public sealed class SqliteGainStore(GainStoreOptions options, TimeProvider time) : IGainStore
{
    /// <summary>How long a request-path write waits for the ledger before it gives up; the request goes on unrecorded.</summary>
    public static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(ContentSchema.RequestCommandTimeoutSeconds);

    private readonly SemaphoreSlim _writer = new(1, 1);
    private bool _initialized;

    public async ValueTask RecordAsync(GainHistoryEntry entry, IReadOnlyList<GainFailure> failures, CancellationToken cancellationToken)
    {
        await WriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            await InsertAsync(connection, Kinds.Request, entry.Id, entry.At, entry.Project, entry.Provider,
                entry.Method, entry.Rule, entry.Layer, entry.Pipeline, JsonSerializer.Serialize(entry), transaction, cancellationToken);
            foreach (var failure in failures)
            {
                await InsertAsync(connection, Kinds.Failure, Guid.NewGuid().ToString("N"), failure.At, failure.Project,
                    entry.Provider, entry.Method, entry.Rule, entry.Layer, entry.Pipeline,
                    JsonSerializer.Serialize(failure), transaction, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }, WritePath.Request, cancellationToken);
    }

    public ValueTask RecallAsync(GainRecall recall, string? provider, string method, string rule, string layer, string pipeline, CancellationToken cancellationToken) =>
        WriteAsync(connection => InsertAsync(connection, Kinds.Recall, Guid.NewGuid().ToString("N"), recall.At,
            recall.Project, provider, method, rule, layer, pipeline, JsonSerializer.Serialize(recall), null, cancellationToken), WritePath.Request, cancellationToken);

    public async ValueTask<GainReport> QueryAsync(GainQuery query, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = Open();
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT kind AS Kind,method AS Method,rule AS Rule,layer AS Layer,pipeline AS Pipeline,payload AS Payload FROM gain_event WHERE " + Predicate,
            Parameters(query), cancellationToken: cancellationToken));
        var requests = new List<GainHistoryEntry>();
        var failures = new List<GainFailure>();
        var recalls = new List<GainRecallEvent>();
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case Kinds.Request when JsonSerializer.Deserialize<GainHistoryEntry>(row.Payload) is { } request: requests.Add(request); break;
                case Kinds.Failure when JsonSerializer.Deserialize<GainFailure>(row.Payload) is { } failure: failures.Add(failure); break;
                case Kinds.Recall when JsonSerializer.Deserialize<GainRecall>(row.Payload) is { } recall: recalls.Add(new GainRecallEvent(recall, new GainPath(row.Method, row.Rule, row.Layer, row.Pipeline))); break;
            }
        }
        return GainReportBuilder.Build(query, time.GetUtcNow(), requests, failures, recalls);
    }

    public async ValueTask<GainResetResult> ResetAsync(GainQuery query, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow();
        long removed = 0;
        await WriteAsync(async connection =>
        {
            // The sequence snapshot excludes a later insert even if its event timestamp is backdated.
            using var transaction = connection.BeginTransaction();
            var snapshot = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COALESCE(MAX(sequence),0) FROM gain_event", transaction: transaction, cancellationToken: cancellationToken));
            var parameters = new DynamicParameters(Parameters(query));
            parameters.Add("Cutoff", cutoff.UtcTicks);
            parameters.Add("Snapshot", snapshot);
            removed = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM gain_event WHERE sequence <= @Snapshot AND at <= @Cutoff AND " + Predicate,
                parameters, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }, WritePath.Reset, cancellationToken);
        return new GainResetResult(removed, cutoff);
    }

    private const string Predicate = """
        (@Project IS NULL OR project = @Project) AND (@From IS NULL OR at >= @From) AND (@To IS NULL OR at < @To)
        AND (@Provider IS NULL OR provider = @Provider) AND (@Method IS NULL OR method = @Method)
        AND (@Rule IS NULL OR rule = @Rule) AND (@Layer IS NULL OR layer = @Layer) AND (@Pipeline IS NULL OR pipeline = @Pipeline)
        """;

    private static object Parameters(GainQuery query) => new
    {
        query.Project, From = query.From?.UtcTicks, To = query.To?.UtcTicks,
        query.Provider, query.Method, query.Rule, query.Layer, query.Pipeline,
    };

    private static async Task InsertAsync(SqliteConnection connection, string kind, string id, DateTimeOffset at,
        string? project, string? provider, string method, string rule, string layer, string pipeline,
        string payload, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO gain_event(kind,id,at,project,provider,method,rule,layer,pipeline,payload)
            VALUES(@kind,@id,@at,@project,@provider,@method,@rule,@layer,@pipeline,@payload)
            """, new { kind, id, at = at.UtcTicks, project, provider, method, rule, layer, pipeline, payload },
            transaction, ContentSchema.RequestCommandTimeoutSeconds, cancellationToken: cancellationToken));
    }

    private async ValueTask WriteAsync(Func<SqliteConnection, Task> write, WritePath path, CancellationToken cancellationToken)
    {
        if (path == WritePath.Reset)
        {
            await _writer.WaitAsync(cancellationToken);
        }
        else if (!await _writer.WaitAsync(RequestWait, cancellationToken))
        {
            throw new TimeoutException("The gain ledger is busy; this request is not recorded.");
        }

        try
        {
            await using var connection = Open();
            if (path == WritePath.Request)
            {
                connection.DefaultTimeout = ContentSchema.RequestCommandTimeoutSeconds;
            }

            await InitializeAsync(connection, cancellationToken);
            await write(connection);
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_initialized) { return; }
        await WriteAsync(static _ => Task.CompletedTask, WritePath.Reset, cancellationToken);
    }

    private SqliteConnection Open()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        return ContentSchema.Open(options.DatabasePath);
    }

    private async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_initialized) { return; }
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS gain_event (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL,id TEXT NOT NULL,at INTEGER NOT NULL,
                project TEXT,provider TEXT,method TEXT NOT NULL,rule TEXT NOT NULL,layer TEXT NOT NULL,pipeline TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS gain_at ON gain_event(at);
            CREATE INDEX IF NOT EXISTS gain_project_at ON gain_event(project,at);
            """, cancellationToken: cancellationToken));
        _initialized = true;
    }

    private sealed record Row(string Kind, string Method, string Rule, string Layer, string Pipeline, string Payload);

    /// <summary>Whether a write is on a proxied request's path, which must not wait long, or is a reset, which may.</summary>
    private enum WritePath
    {
        Request,
        Reset,
    }

    /// <summary>The kinds of ledger rows.</summary>
    private static class Kinds
    {
        public const string Request = "request";
        public const string Failure = "failure";
        public const string Recall = "recall";
    }
}
