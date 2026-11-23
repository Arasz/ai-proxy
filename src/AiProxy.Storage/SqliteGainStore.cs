using System.Text.Json;
using AiProxy.Core.Gain;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>The metadata ledger's private database path.</summary>
/// <param name="DatabasePath">The path of the database file.</param>
/// <param name="OnOpen">Called with each connection the store opens; a seam for tests that watch the statements it runs.</param>
public sealed record GainStoreOptions(string DatabasePath, Action<SqliteConnection>? OnOpen = null);

/// <summary>Persists content-free statistics separately from originals and optional captures.</summary>
public sealed class SqliteGainStore(GainStoreOptions options, TimeProvider time) : IGainStore
{
    /// <summary>How long a request-path writing waits for the ledger before it gives up; the request goes on unrecorded.</summary>
    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(ContentSchema.RequestCommandTimeoutSeconds);

    /// <summary>The file's name, for the error of a file that a newer build wrote.</summary>
    public const string FileName = "gain.db";

    private static readonly string[] Attributions = ["method", "rule", "layer", "pipeline"];

    private static readonly SchemaLadder Ladder = new([CreateEventTable, CreateResetAuditTable]);

    private readonly SemaphoreSlim _writer = new(1, 1);
    private bool _initialized;
    private CachedReport? _cached;

    public async ValueTask RecordAsync(GainHistoryEntry entry, IReadOnlyList<GainFailure> failures, CancellationToken cancellationToken) =>
        await WriteAsync(async connection =>
        {
            await using var transaction = connection.BeginTransaction();
            await InsertAsync(connection, new GainEventInput(Kinds.Request, entry.Id, entry.At, entry.Project, entry.Provider,
                entry.Method, entry.Rule, entry.Layer, entry.Pipeline, JsonSerializer.Serialize(entry)), transaction, cancellationToken);
            foreach (var failure in failures)
            {
                await InsertAsync(connection, new GainEventInput(Kinds.Failure, Guid.CreateVersion7().ToString("N"), failure.At, failure.Project,
                    entry.Provider, entry.Method, entry.Rule, entry.Layer, entry.Pipeline, JsonSerializer.Serialize(failure)), transaction, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }, WritePath.Request, cancellationToken);

    public ValueTask RecordOtherAsync(GainOtherEntry entry, CancellationToken cancellationToken) =>
        WriteAsync(connection => InsertAsync(connection, new GainEventInput(Kinds.Other, entry.Id, entry.At, entry.Project, entry.Provider,
                entry.Method, GainLabels.Rules.OtherTraffic, GainLabels.Layers.None, GainLabels.Pipelines.Passthrough, JsonSerializer.Serialize(entry)), null, cancellationToken),
            WritePath.Request, cancellationToken);

    public ValueTask RecallAsync(GainRecall recall, string? provider, string method, string rule, string layer, string pipeline, CancellationToken cancellationToken) =>
        WriteAsync(connection => InsertAsync(connection, new GainEventInput(Kinds.Recall, Guid.CreateVersion7().ToString("N"), recall.At,
            recall.Project, provider, method, rule, layer, pipeline, JsonSerializer.Serialize(recall)), null, cancellationToken), WritePath.Request, cancellationToken);

    public ValueTask FollowUpAsync(GainFollowUp followUp, string? provider, string method, string rule, string layer, string pipeline, CancellationToken cancellationToken) =>
        WriteAsync(connection => InsertAsync(connection, new GainEventInput(Kinds.FollowUp, Guid.CreateVersion7().ToString("N"), followUp.At,
            followUp.Project, provider, method, rule, layer, pipeline, JsonSerializer.Serialize(followUp)), null, cancellationToken), WritePath.Request, cancellationToken);

    public async ValueTask<GainReport> QueryAsync(GainQuery query, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = Open();
        connection.CreateCollation("ordinal", string.CompareOrdinal);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var version = await connection.QuerySingleAsync<LedgerVersion>(new CommandDefinition(
            Sql.Version, transaction: transaction, cancellationToken: cancellationToken));
        if (Volatile.Read(ref _cached) is { } cached && cached.Query == query && cached.Version == version)
        {
            return cached.Report with { GeneratedAt = time.GetUtcNow(), Filters = query };
        }

        var rows = (await connection.QueryAsync<AggregateRow>(new CommandDefinition(
            Sql.Report, query.ToSqlParameters(), transaction, cancellationToken: cancellationToken))).ToArray();
        var report = Report(query, rows);
        Volatile.Write(ref _cached, new CachedReport(query, version, report));
        return report;
    }

    private GainReport Report(GainQuery query, AggregateRow[] rows)
    {
        var history = Details<GainHistoryEntry>(rows, Views.History);
        var failures = Details<GainFailure>(rows, Views.Failure);
        var recalls = Details<GainRecall>(rows, Views.Recall);
        var other = JsonSerializer.Deserialize<GainOtherTraffic>(rows.Single(static row => row.View == Views.Other).Payload)
                    ?? throw new InvalidDataException($"Failed to deserialize {nameof(GainOtherTraffic)}");

        var attributions = Attributions.SelectMany(dimension => GainReportBuilder.Attribute(dimension,
                rows.Where(row => row.View == dimension).Select(row => new GainAttribution(dimension, row.Name ?? "", row.ConvertToTotals()))))
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
            Recalls = recalls,
            OtherTraffic = other,
            Harnesses = GainReportBuilder.Harnesses(rows.Where(static row => row.View == "provider")
                .Select(static row => new GainProviderTotals(row.Name, row.ConvertToTotals(), row.Host.Length == 0 ? null : row.Host)))
        };
    }

    public async ValueTask<GainResetResult> ResetAsync(GainQuery query, string caller, CancellationToken cancellationToken)
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
            await connection.ExecuteAsync(new CommandDefinition(Sql.InsertResetAudit,
                new { at = cutoff.UtcTicks, filter = JsonSerializer.Serialize(query), removed, caller },
                transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }, WritePath.Reset, cancellationToken);
        return new GainResetResult(removed, cutoff);
    }

    private static GainBucket[] Buckets(IEnumerable<AggregateRow> rows, string view) =>
    [
        .. rows.Where(row => row.View == view).OrderBy(static row => row.Start)
            .Select(static row => new GainBucket(new DateTimeOffset(row.Start, TimeSpan.Zero), row.ConvertToTotals()))
    ];

    private static T[] Details<T>(AggregateRow[] rows, string view) where T : class =>
    [
        .. rows.Where(row => row.View == view).OrderBy(static row => row.Start)
            .Select(static row => JsonSerializer.Deserialize<T>(row.Payload)).OfType<T>()
    ];

    private static async Task InsertAsync(SqliteConnection connection, GainEventInput gainEvent,
        SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(Sql.Insert, gainEvent.ToSqlParameters(),
            transaction, ContentSchema.RequestCommandTimeoutSeconds, cancellationToken: cancellationToken));

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

        var connection = ContentSchema.Open(options.DatabasePath);
        options.OnOpen?.Invoke(connection);
        return connection;
    }

    private async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        Ladder.Apply(connection, FileName, connection.DefaultTimeout);
        _initialized = true;
    }

    private static void CreateEventTable(SqliteConnection connection)
    {
        connection.Execute(Sql.Schema);
        connection.Execute(Sql.NameLayers);
    }

    private static void CreateResetAuditTable(SqliteConnection connection) => connection.Execute(Sql.ResetAuditSchema);

    /// <summary>One row of the report statement: an aggregate's sums, or a detail row's payload text.</summary>
    private sealed record AggregateRow
    {
        public string View { get; init; } = "";
        public long Start { get; init; }
        public string? Name { get; init; }
        public string Host { get; init; } = "";
        public string Payload { get; init; } = "";
        public long Requests { get; init; }
        public long OriginalCharacters { get; init; }
        public long ForwardedCharacters { get; init; }
        public long OriginalBytes { get; init; }
        public long ForwardedBytes { get; init; }
        public long EstimatedOriginalTokens { get; init; }
        public long EstimatedForwardedTokens { get; init; }
        public long EstimatedSavedTokens { get; init; }
        public long Recalls { get; init; }
        public long RetrievedCharacters { get; init; }
        public long EstimatedRetrievedTokens { get; init; }
        public long EstimatedToolTokens { get; init; }
        public long FollowUps { get; init; }
        public long FollowUpBytes { get; init; }
        public long EstimatedFollowUpTokens { get; init; }
        public long FollowUpMeasuredRequests { get; init; }

        public GainTotals ConvertToTotals() => new(Requests, OriginalCharacters, ForwardedCharacters, OriginalBytes, ForwardedBytes,
            EstimatedOriginalTokens, EstimatedForwardedTokens, EstimatedSavedTokens, Recalls, RetrievedCharacters, EstimatedRetrievedTokens,
            EstimatedToolTokens, FollowUps, FollowUpBytes, EstimatedFollowUpTokens, FollowUpMeasuredRequests);
    }

    /// <summary>The ledger's state: an insert raises <paramref name="Last"/>, a delete lowers <paramref name="Rows"/>, a schema step that rewrites rows raises <paramref name="Schema"/>.</summary>
    private sealed record LedgerVersion(long Last, long Rows, long Schema);

    /// <summary>The last report the store built, for the query and ledger version it was built from.</summary>
    private sealed record CachedReport(GainQuery Query, LedgerVersion Version, GainReport Report);

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

        /// <summary>Gives rows written before named layers the layer and pipeline current writes use; a no-op once applied.</summary>
        public const string NameLayers = $"""
                                          UPDATE gain_event SET layer = '{GainLabels.Layers.Retrieval}',pipeline = '{GainLabels.Pipelines.Ccr}'
                                          WHERE kind IN ('{Kinds.Recall}','{Kinds.FollowUp}') AND (layer <> '{GainLabels.Layers.Retrieval}' OR pipeline <> '{GainLabels.Pipelines.Ccr}');
                                          UPDATE gain_event SET layer = CASE
                                              WHEN kind = '{Kinds.Other}' OR rule IN ('{GainLabels.Rules.Unchanged}','{GainLabels.Rules.CcrPlanFailed}') THEN '{GainLabels.Layers.None}'
                                              WHEN rule = '{GainLabels.Rules.CcrTool}' THEN '{GainLabels.Layers.CcrTool}'
                                              ELSE '{GainLabels.Layers.ToolResult}' END
                                          WHERE layer = '{GainLabels.Unknown}';
                                          """;

        public const string ResetAuditSchema = """
                                               CREATE TABLE gain_reset (
                                                   sequence INTEGER PRIMARY KEY AUTOINCREMENT,at INTEGER NOT NULL,filter TEXT NOT NULL,removed INTEGER NOT NULL,caller TEXT NOT NULL);
                                               """;

        public const string InsertResetAudit = "INSERT INTO gain_reset(at,filter,removed,caller) VALUES(@at,@filter,@removed,@caller)";

        public const string Insert = """
                                     INSERT INTO gain_event(kind,id,at,project,provider,method,rule,layer,pipeline,payload)
                                     VALUES(@Kind,@Id,@at,@Project,@Provider,@Method,@Rule,@Layer,@Pipeline,@Payload)
                                     """;

        public const string SequenceSnapshot = "SELECT COALESCE(MAX(sequence),0) FROM gain_event";

        public const string DeleteThroughSnapshot = $"""
                                                     DELETE FROM gain_event WHERE sequence <= @Snapshot AND at <= @Cutoff AND {Predicate}
                                                     """;

        public const string Predicate = """
                                        (@Project IS NULL OR project = @Project) AND (@From IS NULL OR at >= @From) AND (@To IS NULL OR at < @To)
                                        AND (@Provider IS NULL OR provider = @Provider) AND (@Method IS NULL OR method = @Method)
                                        AND (@Rule IS NULL OR rule = @Rule OR (instr(@Rule, ',') = 0 AND instr(',' || rule || ',', ',' || @Rule || ',') > 0)) AND (@Layer IS NULL OR layer = @Layer) AND (@Pipeline IS NULL OR pipeline = @Pipeline)
                                        """;

        public const string Version = "SELECT COALESCE(MAX(sequence),0) AS Last,COUNT(*) AS Rows,(SELECT user_version FROM pragma_user_version) AS Schema FROM gain_event";

        /// <summary>The totals fields, with what a follow-up and a recall row hold in each; a request row reads them from its <c>Totals</c>.</summary>
        private static readonly (string Name, string FollowUp, string Recall)[] Fields =
        [
            ("Requests", "NULL", "NULL"), ("OriginalCharacters", "NULL", "NULL"), ("ForwardedCharacters", "NULL", "NULL"),
            ("OriginalBytes", "NULL", "NULL"), ("ForwardedBytes", "NULL", "NULL"), ("EstimatedOriginalTokens", "NULL", "NULL"),
            ("EstimatedForwardedTokens", "NULL", "NULL"), ("EstimatedSavedTokens", "NULL", "NULL"),
            ("Recalls", "NULL", "1"), ("RetrievedCharacters", "NULL", "json_extract(payload,'$.Characters')"),
            ("EstimatedRetrievedTokens", "NULL", "json_extract(payload,'$.EstimatedTokens')"), ("EstimatedToolTokens", "NULL", "NULL"),
            ("FollowUps", "1", "NULL"), ("FollowUpBytes", "json_extract(payload,'$.Bytes')", "NULL"),
            ("EstimatedFollowUpTokens", "json_extract(payload,'$.EstimatedTokens')", "NULL"), ("FollowUpMeasuredRequests", "NULL", "NULL")
        ];

        /// <summary>The summary counts recall cost from recall rows only.</summary>
        private static readonly string[] RecallOnly = ["Recalls", "RetrievedCharacters", "EstimatedRetrievedTokens"];

        private static readonly string Zeros = string.Concat(Fields.Select(static field => $",0 AS {field.Name}"));

        private static readonly string Sums = string.Join(",", Fields.Select(static field => $"COALESCE(SUM({field.Name}),0) AS {field.Name}"));

        private static readonly string SummarySums = string.Join(",", Fields.Select(static field => RecallOnly.Contains(field.Name)
            ? $"COALESCE(SUM(CASE WHEN kind = '{Kinds.Recall}' THEN {field.Name} ELSE 0 END),0) AS {field.Name}"
            : $"COALESCE(SUM({field.Name}),0) AS {field.Name}"));

        /// <summary>Each value is read from the payload once, here; every pass below sums the columns.</summary>
        private static readonly string MetricsSelect = $"""
                                                        SELECT kind,at,provider,method,rule,layer,pipeline,
                                                            COALESCE(json_extract(payload,'$.Host'),'') AS host,
                                                            {string.Join(",", Fields.Select(static field =>
                                                                $"CASE kind WHEN '{Kinds.Request}' THEN json_extract(payload,'$.Totals.{field.Name}') WHEN '{Kinds.FollowUp}' THEN {field.FollowUp} ELSE {field.Recall} END AS {field.Name}"))}
                                                        FROM gain_event WHERE kind IN ('{Kinds.Request}','{Kinds.Recall}','{Kinds.FollowUp}') AND{Predicate}
                                                        """;

        private static readonly string Aggregate = $"""
                                                    WITH metrics AS MATERIALIZED ({MetricsSelect})
                                                    SELECT 'summary' AS View,0 AS Start,'' AS Name,'' AS Host,'' AS Payload,{SummarySums} FROM metrics
                                                    UNION ALL SELECT 'daily',at - at % 864000000000,'','','',{Sums} FROM metrics WHERE kind = '{Kinds.Request}' GROUP BY at - at % 864000000000
                                                    UNION ALL SELECT 'weekly',(at / 864000000000 - at / 864000000000 % 7) * 864000000000,'','','',{Sums} FROM metrics WHERE kind = '{Kinds.Request}' GROUP BY (at / 864000000000 - at / 864000000000 % 7) * 864000000000
                                                    UNION ALL SELECT 'monthly',(CAST(strftime('%s',at / 10000000 - 62135596800,'unixepoch','start of month') AS INTEGER) + 62135596800) * 10000000,'','','',{Sums} FROM metrics WHERE kind = '{Kinds.Request}' GROUP BY strftime('%Y-%m',at / 10000000 - 62135596800,'unixepoch')
                                                    UNION ALL SELECT dimension,0,CASE dimension WHEN 'method' THEN method WHEN 'rule' THEN rule WHEN 'layer' THEN layer ELSE pipeline END AS Name,'','',{Sums} FROM metrics CROSS JOIN (SELECT 'method' AS dimension UNION ALL SELECT 'rule' UNION ALL SELECT 'layer' UNION ALL SELECT 'pipeline')
                                                    GROUP BY dimension,Name
                                                    UNION ALL SELECT 'provider',0,provider,host,'',{Sums} FROM metrics GROUP BY provider,host
                                                    """;

        /// <summary>The whole report in one statement: the aggregate rows, then the newest payloads of three kinds (position in <c>Start</c>), then the other-traffic count.</summary>
        public static readonly string Report = $"""
                                      {Aggregate}
                                      UNION ALL SELECT '{Views.History}',Position,'','',payload{Zeros} FROM (
                                          SELECT payload,ROW_NUMBER() OVER (ORDER BY at DESC,id COLLATE ordinal,sequence) AS Position FROM (
                                              SELECT payload,at,id,sequence FROM gain_event WHERE kind = '{Kinds.Request}' AND {Predicate} ORDER BY at DESC,id COLLATE ordinal,sequence LIMIT @HistoryLimit))
                                      UNION ALL SELECT '{Views.Failure}',Position,'','',payload{Zeros} FROM (
                                          SELECT payload,ROW_NUMBER() OVER (ORDER BY at DESC,sequence) AS Position FROM (
                                              SELECT payload,at,sequence FROM gain_event WHERE kind = '{Kinds.Failure}' AND {Predicate} ORDER BY at DESC,sequence LIMIT @HistoryLimit))
                                      UNION ALL SELECT '{Views.Recall}',Position,'','',payload{Zeros} FROM (
                                          SELECT payload,ROW_NUMBER() OVER (ORDER BY at DESC,sequence) AS Position FROM (
                                              SELECT payload,at,sequence FROM gain_event WHERE kind = '{Kinds.Recall}' AND {Predicate} ORDER BY at DESC,sequence LIMIT @HistoryLimit))
                                      UNION ALL SELECT '{Views.Other}',0,'','',json_object('Requests',COUNT(*),'Bytes',COALESCE(SUM(json_extract(payload,'$.Bytes')),0)){Zeros}
                                          FROM gain_event WHERE kind = '{Kinds.Other}' AND {Predicate}
                                      """;
    }

    /// <summary>Whether a write is on a proxied request's path, which must not wait long, or is a reset, which may.</summary>
    private enum WritePath
    {
        Request,
        Reset
    }

    /// <summary>The row types of the report statement that are not aggregates.</summary>
    private static class Views
    {
        public const string History = "history";
        public const string Failure = "failure";
        public const string Recall = "recall";
        public const string Other = "other";
    }

    /// <summary>The kinds of ledger rows.</summary>
    private static class Kinds
    {
        public const string Request = "request";
        public const string Failure = "failure";
        public const string Recall = "recall";
        public const string FollowUp = "followup";
        public const string Other = "other";
    }
}

file static class GainStoreExtensions
{
    extension(GainQuery query)
    {
        public object ToSqlParameters() =>
            new
            {
                query.Project, From = query.From?.UtcTicks, To = query.To?.UtcTicks,
                query.Provider, query.Method, query.Rule, query.Layer, query.Pipeline, HistoryLimit = Math.Max(0, query.HistoryLimit)
            };
    }

    extension(SqliteGainStore.GainEventInput gainEvent)
    {
        public object ToSqlParameters() =>
            new
            {
                gainEvent.Kind, gainEvent.Id, at = gainEvent.At.UtcTicks, gainEvent.Project, gainEvent.Provider,
                gainEvent.Method, gainEvent.Rule, gainEvent.Layer, gainEvent.Pipeline, gainEvent.Payload
            };
    }
}