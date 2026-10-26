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
    /// <summary>How long a request-path writing waits for the ledger before it gives up; the request goes on unrecorded.</summary>
    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(ContentSchema.RequestCommandTimeoutSeconds);

    private static readonly string[] Attributions = ["method", "rule", "layer", "pipeline"];

    private readonly SemaphoreSlim _writer = new(1, 1);
    private bool _initialized;

    public async ValueTask RecordAsync(GainHistoryEntry entry, IReadOnlyList<GainFailure> failures, CancellationToken cancellationToken)
    {
        await WriteAsync(async connection =>
        {
            await using var transaction = connection.BeginTransaction();
            await InsertAsync(connection, new GainEventInput(Kinds.Request, entry.Id, entry.At, entry.Project, entry.Provider,
                entry.Method, entry.Rule, entry.Layer, entry.Pipeline, JsonSerializer.Serialize(entry)), transaction, cancellationToken);
            foreach (var failure in failures)
                await InsertAsync(connection, new GainEventInput(Kinds.Failure, Guid.CreateVersion7().ToString("N"), failure.At, failure.Project,
                    entry.Provider, entry.Method, entry.Rule, entry.Layer, entry.Pipeline, JsonSerializer.Serialize(failure)), transaction, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }, WritePath.Request, cancellationToken);
    }

    public ValueTask RecallAsync(GainRecall recall, string? provider, string method, string rule, string layer, string pipeline, CancellationToken cancellationToken)
    {
        return WriteAsync(connection => InsertAsync(connection, new GainEventInput(Kinds.Recall, Guid.CreateVersion7().ToString("N"), recall.At,
            recall.Project, provider, method, rule, layer, pipeline, JsonSerializer.Serialize(recall)), null, cancellationToken), WritePath.Request, cancellationToken);
    }

    public async ValueTask<GainReport> QueryAsync(GainQuery query, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = Open();
        connection.CreateCollation("ordinal", string.CompareOrdinal);
        await using var transaction = connection.BeginTransaction(true);
        var parameters = query.ToSqlParameters();
        var rows = (await connection.QueryAsync<AggregateRow>(new CommandDefinition(
            Sql.Aggregate, parameters, transaction, cancellationToken: cancellationToken))).ToArray();
        var history = await DetailsAsync<GainHistoryEntry>(connection, transaction, parameters, Kinds.Request,
            Sql.History, cancellationToken);
        var failures = await DetailsAsync<GainFailure>(connection, transaction, parameters, Kinds.Failure,
            Sql.Events, cancellationToken);
        var recalls = await DetailsAsync<GainRecall>(connection, transaction, parameters, Kinds.Recall,
            Sql.Events, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var attributions = Attributions.SelectMany(dimension =>
                rows.Where(row => row.View == dimension)
                    .OrderBy(static row => row.Name, StringComparer.Ordinal)
                    .Select(row => new GainAttribution(dimension, row.Name, row.ConvertToTotals())))
            .ToArray();
        return GainReportBuilder.Build(query, time.GetUtcNow(), [], [], []) with
        {
            Summary = rows.Single(static row => row.View == "summary").ConvertToTotals(),
            History = history,
            Daily = Buckets(rows, "daily"),
            Weekly = Buckets(rows, "weekly"),
            Monthly = Buckets(rows, "monthly"),
            Attributions = attributions,
            Failures = failures,
            Recalls = recalls
        };
    }

    public async ValueTask<GainResetResult> ResetAsync(GainQuery query, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow();
        long removed = 0;
        await WriteAsync(async connection =>
        {
            // The sequence snapshot excludes a later insert even if its event timestamp is backdated.
            await using var transaction = connection.BeginTransaction();
            var snapshot = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                Sql.SequenceSnapshot, transaction: transaction, cancellationToken: cancellationToken));
            var parameters = new DynamicParameters(query.ToSqlParameters());
            parameters.Add("Cutoff", cutoff.UtcTicks);
            parameters.Add("Snapshot", snapshot);
            removed = await connection.ExecuteAsync(new CommandDefinition(
                Sql.DeleteThroughSnapshot, parameters,
                transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }, WritePath.Reset, cancellationToken);
        return new GainResetResult(removed, cutoff);
    }

    private static GainBucket[] Buckets(IEnumerable<AggregateRow> rows, string view)
    {
        return
        [
            .. rows.Where(row => row.View == view).OrderBy(static row => row.Start)
                .Select(static row => new GainBucket(new DateTimeOffset(row.Start, TimeSpan.Zero), row.ConvertToTotals()))
        ];
    }

    private static async Task<T[]> DetailsAsync<T>(SqliteConnection connection, SqliteTransaction transaction,
        object filters, string kind, string sql, CancellationToken cancellationToken) where T : class
    {
        var parameters = new DynamicParameters(filters);
        parameters.Add("Kind", kind);
        var payloads = await connection.QueryAsync<string>(new CommandDefinition(
            sql,
            parameters, transaction, cancellationToken: cancellationToken));
        return payloads.Select(static payload => JsonSerializer.Deserialize<T>(payload)).OfType<T>().ToArray();
    }

    private static async Task InsertAsync(SqliteConnection connection, GainEventInput gainEvent,
        SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(Sql.Insert, gainEvent.ToSqlParameters(),
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
        if (_initialized)
        {
            return;
        }

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
        if (_initialized)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(Sql.Schema, cancellationToken: cancellationToken));
        _initialized = true;
    }

    private sealed record AggregateRow(string View, long Start, string Name, string Totals)
    {
        public GainTotals ConvertToTotals()
        {
            return JsonSerializer.Deserialize<GainTotals>(Totals) ?? throw new InvalidDataException($"Failed to deserialize {nameof(GainTotals)}");
        }
    }

    internal sealed record GainEventInput(
        string Kind,
        string Id,
        DateTimeOffset At,
        string? Project,
        string? Provider,
        string Method,
        string Rule,
        string Layer,
        string Pipeline,
        string Payload);

    private static class Sql
    {
        public const string Schema = """
                                     CREATE TABLE IF NOT EXISTS gain_event (
                                         sequence INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL,id TEXT NOT NULL,at INTEGER NOT NULL,
                                         project TEXT,provider TEXT,method TEXT NOT NULL,rule TEXT NOT NULL,layer TEXT NOT NULL,pipeline TEXT NOT NULL,payload TEXT NOT NULL);
                                     CREATE INDEX IF NOT EXISTS gain_at ON gain_event(at);
                                     CREATE INDEX IF NOT EXISTS gain_project_at ON gain_event(project,at);
                                     """;

        public const string Insert = """
                                     INSERT INTO gain_event(kind,id,at,project,provider,method,rule,layer,pipeline,payload)
                                     VALUES(@Kind,@Id,@at,@Project,@Provider,@Method,@Rule,@Layer,@Pipeline,@Payload)
                                     """;

        public const string SequenceSnapshot = "SELECT COALESCE(MAX(sequence),0) FROM gain_event";

        public const string DeleteThroughSnapshot = $"""
                                                     DELETE FROM gain_event WHERE sequence <= @Snapshot AND at <= @Cutoff AND {Predicate}
                                                     """;

        public const string History = $"""
                                       SELECT payload FROM gain_event WHERE kind = @Kind AND {Predicate} ORDER BY at DESC,id COLLATE ordinal,sequence LIMIT @HistoryLimit
                                       """;

        public const string Events = $"""
                                      SELECT payload FROM gain_event WHERE kind = @Kind AND {Predicate} ORDER BY at DESC,sequence LIMIT @HistoryLimit
                                      """;

        public const string Predicate = """
                                        (@Project IS NULL OR project = @Project) AND (@From IS NULL OR at >= @From) AND (@To IS NULL OR at < @To)
                                        AND (@Provider IS NULL OR provider = @Provider) AND (@Method IS NULL OR method = @Method)
                                        AND (@Rule IS NULL OR rule = @Rule) AND (@Layer IS NULL OR layer = @Layer) AND (@Pipeline IS NULL OR pipeline = @Pipeline)
                                        """;

        private const string SumJson = """
                                       json_object(
                                           'Requests',COALESCE(SUM(json_extract(totals,'$.Requests')),0),
                                           'OriginalCharacters',COALESCE(SUM(json_extract(totals,'$.OriginalCharacters')),0),
                                           'ForwardedCharacters',COALESCE(SUM(json_extract(totals,'$.ForwardedCharacters')),0),
                                           'OriginalBytes',COALESCE(SUM(json_extract(totals,'$.OriginalBytes')),0),
                                           'ForwardedBytes',COALESCE(SUM(json_extract(totals,'$.ForwardedBytes')),0),
                                           'EstimatedOriginalTokens',COALESCE(SUM(json_extract(totals,'$.EstimatedOriginalTokens')),0),
                                           'EstimatedForwardedTokens',COALESCE(SUM(json_extract(totals,'$.EstimatedForwardedTokens')),0),
                                           'EstimatedSavedTokens',COALESCE(SUM(json_extract(totals,'$.EstimatedSavedTokens')),0),
                                           'Recalls',COALESCE(SUM(json_extract(totals,'$.Recalls')),0),
                                           'RetrievedCharacters',COALESCE(SUM(json_extract(totals,'$.RetrievedCharacters')),0),
                                           'EstimatedRetrievedTokens',COALESCE(SUM(json_extract(totals,'$.EstimatedRetrievedTokens')),0))
                                       """;

        public const string Aggregate = $"""
                                         WITH metrics AS (
                                             SELECT kind,at,method,rule,layer,pipeline,
                                                 CASE kind WHEN 'request' THEN json_extract(payload,'$.Totals')
                                                     ELSE json_object('Recalls',1,'RetrievedCharacters',json_extract(payload,'$.Characters'),
                                                         'EstimatedRetrievedTokens',json_extract(payload,'$.EstimatedTokens')) END AS totals
                                             FROM gain_event WHERE kind IN ('request','recall') AND{Predicate})
                                         SELECT 'summary' AS View,0 AS Start,'' AS Name,json_set({SumJson}    ,'$.Recalls',COALESCE(SUM(CASE WHEN kind = 'recall' THEN 1 ELSE 0 END),0),
                                             '$.RetrievedCharacters',COALESCE(SUM(CASE WHEN kind = 'recall' THEN json_extract(totals,'$.RetrievedCharacters') ELSE 0 END),0),
                                             '$.EstimatedRetrievedTokens',COALESCE(SUM(CASE WHEN kind = 'recall' THEN json_extract(totals,'$.EstimatedRetrievedTokens') ELSE 0 END),0)) AS Totals
                                         FROM metrics
                                         UNION ALL SELECT 'daily',at - at % 864000000000,'',{SumJson}FROM metrics WHERE kind = 'request' GROUP BY at - at % 864000000000
                                         UNION ALL SELECT 'weekly',(at / 864000000000 - at / 864000000000 % 7) * 864000000000,'',{SumJson}FROM metrics WHERE kind = 'request' GROUP BY (at / 864000000000 - at / 864000000000 % 7) * 864000000000
                                         UNION ALL SELECT 'monthly',(CAST(strftime('%s',at / 10000000 - 62135596800,'unixepoch','start of month') AS INTEGER) + 62135596800) * 10000000,'',{SumJson}FROM metrics WHERE kind = 'request' GROUP BY strftime('%Y-%m',at / 10000000 - 62135596800,'unixepoch')
                                         UNION ALL SELECT dimension,0,CASE dimension WHEN 'method' THEN method WHEN 'rule' THEN rule WHEN 'layer' THEN layer ELSE pipeline END AS Name,{SumJson}FROM metrics CROSS JOIN (SELECT 'method' AS dimension UNION ALL SELECT 'rule' UNION ALL SELECT 'layer' UNION ALL SELECT 'pipeline')
                                         GROUP BY dimension,Name
                                         """;
    }

    /// <summary>Whether a write is on a proxied request's path, which must not wait long, or is a reset, which may.</summary>
    private enum WritePath
    {
        Request,
        Reset
    }

    /// <summary>The kinds of ledger rows.</summary>
    private static class Kinds
    {
        public const string Request = "request";
        public const string Failure = "failure";
        public const string Recall = "recall";
    }
}

file static class GainStoreExtensions
{
    extension(GainQuery query)
    {
        public object ToSqlParameters()
        {
            return new
            {
                query.Project, From = query.From?.UtcTicks, To = query.To?.UtcTicks,
                query.Provider, query.Method, query.Rule, query.Layer, query.Pipeline, HistoryLimit = Math.Max(0, query.HistoryLimit)
            };
        }
    }

    extension(SqliteGainStore.GainEventInput gainEvent)
    {
        public object ToSqlParameters()
        {
            return new
            {
                gainEvent.Kind, gainEvent.Id, at = gainEvent.At.UtcTicks, gainEvent.Project, gainEvent.Provider,
                gainEvent.Method, gainEvent.Rule, gainEvent.Layer, gainEvent.Pipeline, gainEvent.Payload
            };
        }
    }
}