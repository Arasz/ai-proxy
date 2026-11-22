using System.Text.Json;
using AiProxy.Core.Gain;
using AiProxy.Storage;
using AiProxy.Tests.Integration;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Gain;

public sealed class GainLedgerTests
{
    [Fact]
    public async Task The_report_on_a_seeded_store_equals_the_report_the_per_view_queries_gave()
    {
        using var directory = new TemporaryDirectory();
        var store = await SeededStoreAsync(Path.Combine(directory.Path, "gain.db"));

        var all = await store.QueryAsync(new GainQuery(HistoryLimit: 50), TestContext.Current.CancellationToken);
        var filtered = await store.QueryAsync(new GainQuery(Rule: "log", HistoryLimit: 2), TestContext.Current.CancellationToken);

        Assert.Equal(Baseline("gain-report-all.json"), JsonSerializer.Serialize(all));
        Assert.Equal(Baseline("gain-report-filtered.json"), JsonSerializer.Serialize(filtered));
    }

    [Fact]
    public async Task The_report_issues_one_query()
    {
        using var directory = new TemporaryDirectory();
        var statements = new Statements();
        var store = await SeededStoreAsync(Path.Combine(directory.Path, "gain.db"), statements.Watch);

        statements.Recording = true;
        await store.QueryAsync(new GainQuery(HistoryLimit: 50), TestContext.Current.CancellationToken);
        statements.Recording = false;

        var queries = statements.Texts.Where(static text => text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                                                            || text.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Single(queries);
    }

    [Fact]
    public async Task A_file_written_by_0_34_0_opens_with_only_the_audit_table_and_the_version_added()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "gain.db");
        await using (var legacy = new SqliteConnection($"Data Source={path}"))
        {
            await legacy.OpenAsync(TestContext.Current.CancellationToken);
            await legacy.ExecuteAsync("""
                                      CREATE TABLE gain_event (
                                          sequence INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL,id TEXT NOT NULL,at INTEGER NOT NULL,
                                          project TEXT,provider TEXT,method TEXT NOT NULL,rule TEXT NOT NULL,layer TEXT NOT NULL,pipeline TEXT NOT NULL,payload TEXT NOT NULL);
                                      CREATE INDEX gain_at ON gain_event(at);
                                      CREATE INDEX gain_project_at ON gain_event(project,at);
                                      INSERT INTO gain_event(kind,id,at,project,provider,method,rule,layer,pipeline,payload)
                                      VALUES('other','o1',1,'/a','anthropic','GET','other_traffic','none','passthrough','{"Bytes":5}');
                                      """);
        }

        var store = new SqliteGainStore(new GainStoreOptions(path), new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T12:00Z")));
        var report = await store.QueryAsync(new GainQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(new GainOtherTraffic(1, 5), report.OtherTraffic);
        await using var check = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await check.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2L, await check.ExecuteScalarAsync<long>("PRAGMA user_version;"));
        Assert.Equal(["gain_event", "gain_reset", "sqlite_sequence"], (await check.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")).ToArray());
        Assert.Equal(1L, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM gain_event WHERE id = 'o1' AND layer = 'none' AND at = 1"));
    }

    [Fact]
    public async Task A_file_from_a_newer_build_refuses_to_open()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "gain.db");
        await using (var newer = new SqliteConnection($"Data Source={path}"))
        {
            await newer.OpenAsync(TestContext.Current.CancellationToken);
            await newer.ExecuteAsync("PRAGMA user_version = 99;");
        }

        var store = new SqliteGainStore(new GainStoreOptions(path), TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.QueryAsync(new GainQuery(), TestContext.Current.CancellationToken));
        Assert.Contains("gain.db", error.Message);
    }

    [Fact]
    public async Task A_reset_writes_one_audit_row_with_the_filter_the_removed_count_and_the_caller()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "gain.db");
        var store = await SeededStoreAsync(path);
        var rowsBefore = await CountEventsAsync(path, "/a", DateTimeOffset.Parse("2026-11-20T12:00Z").UtcTicks);

        var result = await store.ResetAsync(new GainQuery(Project: "/a"), "cli gain --reset pid 1234", TestContext.Current.CancellationToken);

        var row = Assert.Single(await ReadAuditAsync(path));
        Assert.True(rowsBefore > 0);
        Assert.Equal(rowsBefore, result.Removed);
        Assert.Equal(rowsBefore, row.Removed);
        Assert.Equal("cli gain --reset pid 1234", row.Caller);
        Assert.Equal(result.Cutoff.UtcTicks, row.At);
        Assert.Equal("/a", JsonSerializer.Deserialize<GainQuery>(row.Filter)!.Project);
        Assert.Equal(0, await CountEventsAsync(path, "/a", DateTimeOffset.Parse("2026-11-20T12:00Z").UtcTicks));
        Assert.True(await CountEventsAsync(path, "/b") > 0);
    }

    [Fact]
    public async Task A_reset_that_cannot_write_its_audit_row_deletes_nothing()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "gain.db");
        var store = await SeededStoreAsync(path);
        var before = await CountEventsAsync(path, null);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await connection.ExecuteAsync("CREATE TRIGGER block_audit BEFORE INSERT ON gain_reset BEGIN SELECT RAISE(ABORT, 'blocked'); END;");
        }

        await Assert.ThrowsAnyAsync<Exception>(async () => await store.ResetAsync(new GainQuery(), "x", TestContext.Current.CancellationToken));

        Assert.Equal(before, await CountEventsAsync(path, null));
        Assert.Empty(await ReadAuditAsync(path));
    }

    [Fact]
    public async Task A_reset_keeps_a_row_written_after_its_snapshot_even_when_the_row_is_backdated()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "gain.db");
        var late = false;
        var store = await SeededStoreAsync(path, connection => SQLitePCL.raw.sqlite3_trace(connection.Handle, (_, text) =>
        {
            if (late || !text.utf8_to_string().StartsWith("DELETE FROM gain_event", StringComparison.Ordinal))
            {
                return;
            }

            late = true;
            // The insert runs on the reset's own connection and transaction, after the snapshot read and before the delete.
            var result = SQLitePCL.raw.sqlite3_exec(connection.Handle, "INSERT INTO gain_event(kind,id,at,project,provider,method,rule,layer,pipeline,payload) VALUES('other','late',1,'/a','anthropic','GET','other_traffic','none','passthrough','{\"Bytes\":1}')");
            Assert.Equal(SQLitePCL.raw.SQLITE_OK, result);
        }, null));

        await store.ResetAsync(new GainQuery(), "x", TestContext.Current.CancellationToken);

        Assert.True(late);
        await using var check = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await check.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1L, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM gain_event WHERE id = 'late'"));
    }

    internal static async Task<SqliteGainStore> SeededStoreAsync(string path, Action<SqliteConnection>? onOpen = null)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var store = new SqliteGainStore(new GainStoreOptions(path, onOpen), new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T12:00Z")));
        var providers = new (string Provider, string Host)[] { ("anthropic", "api.anthropic.com"), ("openai", "api.openai.com"), ("gemini", "generativelanguage.googleapis.com") };
        var rules = new[] { "log", "diff" };
        for (var index = 0; index < 18; index++)
        {
            var (provider, host) = providers[index % 3];
            var at = DateTimeOffset.Parse("2026-09-28T10:00Z").AddDays(index * 5).AddMinutes(index);
            var original = 1_000 + index * 137;
            var entry = new GainHistoryEntry($"req-{index:D2}", at, index % 2 == 0 ? "/a" : "/b", provider, "POST", rules[index % 2], "tool_result", "content",
                GainReportBuilder.RequestTotals(original, original / 3, original * 2, original / 2), host);
            var failures = index % 5 == 0 ? new[] { new GainFailure(at, entry.Id, entry.Project, "compression", $"Failure {index}") } : [];
            await store.RecordAsync(entry, failures, cancellation);
        }

        await store.RecallAsync(new GainRecall(DateTimeOffset.Parse("2026-11-01T01:00Z"), "p1", "/a", "agent", "whole", 289, 100, null, "api.anthropic.com"), "anthropic", "POST", "log", "retrieval", "ccr", cancellation);
        await store.RecallAsync(new GainRecall(DateTimeOffset.Parse("2026-11-02T01:00Z"), "p2", "/b", "agent", "whole", 500, 150, null), "openai", "POST", "diff", "retrieval", "ccr", cancellation);
        await store.FollowUpAsync(new GainFollowUp(DateTimeOffset.Parse("2026-11-01T02:00Z"), "/a", 1, 1_000, 1_100, 346, "api.anthropic.com"), "anthropic", "POST", "log", "retrieval", "ccr", cancellation);
        await store.RecordOtherAsync(new GainOtherEntry("o1", DateTimeOffset.Parse("2026-11-03T01:00Z"), "/a", "anthropic", "GET", 70), cancellation);
        await store.RecordOtherAsync(new GainOtherEntry("o2", DateTimeOffset.Parse("2026-11-04T01:00Z"), "/b", "openai", "GET", 30), cancellation);
        return store;
    }

    private static string Baseline(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Unit", "Gain", "Baselines", name)).TrimEnd('\n');

    private static async Task<long> CountEventsAsync(string path, string? project, long atOrBefore = long.MaxValue)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM gain_event WHERE (@project IS NULL OR project = @project) AND at <= @atOrBefore", new { project, atOrBefore });
    }

    private static async Task<AuditRow[]> ReadAuditAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return (await connection.QueryAsync<AuditRow>("SELECT at AS At,filter AS Filter,removed AS Removed,caller AS Caller FROM gain_reset ORDER BY sequence")).ToArray();
    }

    private sealed record AuditRow(long At, string Filter, long Removed, string Caller);

    /// <summary>Collects the text of every statement a watched connection starts while recording is on.</summary>
    private sealed class Statements
    {
        private readonly List<string> _texts = [];

        public bool Recording { get; set; }

        public IReadOnlyList<string> Texts => _texts;

        public void Watch(SqliteConnection connection) =>
            SQLitePCL.raw.sqlite3_trace(connection.Handle, static (state, text) =>
            {
                var self = (Statements)state;
                if (self.Recording)
                {
                    self._texts.Add(text.utf8_to_string());
                }
            }, this);
    }
}
