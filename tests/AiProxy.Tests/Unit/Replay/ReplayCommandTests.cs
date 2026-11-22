using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiProxy.Cli;
using AiProxy.Core.Replay;
using AiProxy.Replay;
using AiProxy.Storage;
using AiProxy.Tests.Integration;
using AiProxy.Tests.Unit.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using ZstdSharp;

namespace AiProxy.Tests.Unit.Replay;

/// <summary>The replay command reads stored bodies through the real pipeline and never changes the databases.</summary>
public sealed class ReplayCommandTests : IDisposable
{
    private static readonly string Output = string.Join("\n", Enumerable.Range(1, 120).Select(index => $"step {index:D4}: compiled module m{index} into obj/m{index}.o"));
    private static readonly string NumberedOutput = string.Join("\n", Output.Split('\n').Select((line, index) => $"{index + 1,6}\t{line}"));
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-11-10T12:00:00Z");

    private readonly GainCommandTests.Fixture _fixture = new();
    private readonly TemporaryDirectory _home = new();

    public ReplayCommandTests()
    {
        Directory.CreateDirectory(_home.Path);
    }

    public void Dispose()
    {
        _home.Dispose();
        _fixture.Dispose();
    }

    private static readonly string PrettyJson = JsonSerializer.Serialize(
        Enumerable.Range(0, 60).Select(index => new Dictionary<string, object?> { ["id"] = index, ["name"] = $"item{index}", ["tags"] = new[] { "a", "b" }, ["nested"] = new Dictionary<string, object?> { ["x"] = index, ["y"] = null } }),
        new JsonSerializerOptions { WriteIndented = true, IndentSize = 4 });

    private static readonly string CodeFile = $"using System;\n\nnamespace Demo;\n\n/// <summary>The demo type.</summary>\npublic sealed class Example\n{{\n{string.Concat(Enumerable.Range(0, 40).Select(index =>
        $"    /// <summary>Returns the number {index} after doing a fair amount of careful work that the comment goes on to describe at length.</summary>\n    public int Value{index}()\n    {{\n        var total = {index};\n        for (var step = 0; step < 10; step++)\n        {{\n            total += step * {index};\n        }}\n\n        return total;\n    }}\n\n"))}}}\n";

    private static byte[] ReadBody(string path, string output) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            messages = new object[]
            {
                new { role = "assistant", content = new[] { new { type = "tool_use", id = "t1", name = "Read", input = new { file_path = path } } } },
                new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "t1", content = output } } }
            }
        }));

    private static byte[] TwoToolBody(string first, string second) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "t1", content = first }, new { type = "tool_result", tool_use_id = "t2", content = second } } } }
        }));

    private static byte[] ToolBody(string output) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "t1", content = output } } } }
        }));

    private string Db(string file) => Path.Combine(_home.Path, file);

    private void WriteContent(params byte[][] originals) => WriteContentAt(Stamp, originals);

    private void WriteContentAt(DateTimeOffset at, params byte[][] originals)
    {
        using var connection = Create("content.db", "CREATE TABLE IF NOT EXISTS content_request (request_id TEXT PRIMARY KEY, created_at TEXT NOT NULL, original TEXT NOT NULL, compressed TEXT NOT NULL, ratio REAL NOT NULL, path TEXT NOT NULL, chunk_count INTEGER NOT NULL)");
        foreach (var original in originals)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO content_request VALUES (@Id, @At, @Original, '', 1, '', 0)";
            command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("@At", at.ToString("O"));
            command.Parameters.AddWithValue("@Original", Encoding.UTF8.GetString(original));
            command.ExecuteNonQuery();
        }
    }

    private void WriteCapture(params byte[][] bodies) => WriteCaptureAt(Stamp, "api.anthropic.com", bodies);

    private void WriteCaptureAt(DateTimeOffset at, string host, params byte[][] bodies)
    {
        using var connection = Create("capture.db", "CREATE TABLE IF NOT EXISTS capture_request (request_id TEXT PRIMARY KEY, captured_at TEXT NOT NULL, provider TEXT NOT NULL, host TEXT NOT NULL, path TEXT NOT NULL, body BLOB NOT NULL)");
        foreach (var body in bodies)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO capture_request VALUES (@Id, @At, 'anthropic', @Host, '/v1/messages', @Body)";
            command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("@At", at.ToString("O"));
            command.Parameters.AddWithValue("@Host", host);
            command.Parameters.AddWithValue("@Body", body);
            command.ExecuteNonQuery();
        }
    }

    private void WriteGain(params (string Kind, DateTimeOffset At)[] events)
    {
        using var connection = Create("gain.db", "CREATE TABLE gain_event (sequence INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, id TEXT NOT NULL, at INTEGER NOT NULL, project TEXT, provider TEXT, method TEXT NOT NULL, rule TEXT NOT NULL, layer TEXT NOT NULL, pipeline TEXT NOT NULL, payload TEXT NOT NULL)");
        foreach (var (kind, at) in events)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO gain_event (kind, id, at, method, rule, layer, pipeline, payload) VALUES (@Kind, @Id, @At, '', '', '', '', '')";
            command.Parameters.AddWithValue("@Kind", kind);
            command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("@At", at.UtcTicks);
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Create(string file, string ddl)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db(file), Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = ddl;
        command.ExecuteNonQuery();
        return connection;
    }

    private async Task<JsonElement> ReplayAsync(params string[] extra)
    {
        _fixture.Output.GetStringBuilder().Clear();
        var arguments = new[] { "replay", "--home", _home.Path, "--json" }.Concat(extra).ToArray();
        var code = await new CliApplication(_fixture.Runtime).RunAsync(arguments, TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        return JsonDocument.Parse(_fixture.Output.ToString()).RootElement.Clone();
    }

    private string Hashes() => string.Join(",", new[] { "content.db", "capture.db" }.Select(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Db(file))))));

    [Fact]
    public async Task The_minimum_option_reaches_the_pipeline_so_the_same_body_previews_only_when_it_is_low_enough()
    {
        WriteCapture(ToolBody(Output));

        var high = await ReplayAsync("--minimum", "3000");
        var low = await ReplayAsync("--minimum", "300");

        Assert.Equal(1, high.GetProperty("rowsReplayed").GetInt32());
        Assert.Equal(0, high.GetProperty("rowsWithPreview").GetInt32());
        Assert.Equal(1, low.GetProperty("rowsWithPreview").GetInt32());
        var preview = Assert.Single(low.GetProperty("previews").EnumerateArray());
        Assert.Equal(1, preview.GetProperty("sends").GetInt32());
        Assert.Equal(1169, preview.GetProperty("savedTokens").GetInt64());
        Assert.Equal(1169, low.GetProperty("savedTokens").GetInt64());
    }

    [Fact]
    public async Task A_corrupt_zstd_capture_row_is_counted_undecodable_and_not_as_a_row_without_saving()
    {
        var corrupt = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3, 4, 5, 6, 7, 8 };
        WriteCapture(corrupt, ToolBody(Output));

        var result = await ReplayAsync("--minimum", "300", "--include-encoded");

        Assert.Equal(2, result.GetProperty("rowsRead").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsUndecodable").GetInt32());
        Assert.Equal(0, result.GetProperty("rowsNotJson").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsReplayed").GetInt32());
    }

    [Fact]
    public async Task A_good_zstd_capture_row_is_decoded_and_replayed()
    {
        using var compressor = new Compressor();
        WriteCapture(compressor.Wrap(ToolBody(Output)).ToArray());

        var result = await ReplayAsync("--minimum", "300", "--include-encoded");

        Assert.Equal(0, result.GetProperty("rowsUndecodable").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsWithPreview").GetInt32());
    }

    [Fact]
    public async Task Replay_needs_no_api_token_calls_no_api_and_creates_no_gain_database()
    {
        WriteCapture(ToolBody(Output));

        var result = await ReplayAsync("--minimum", "300");

        Assert.Equal(1, result.GetProperty("rowsWithPreview").GetInt32());
        Assert.False(File.Exists(Db(Hosting.Api.ApiToken.FileName)));
        Assert.False(File.Exists(Db(SqliteGainStore.FileName)));
        Assert.Empty(_fixture.Handler.Uris);
    }

    [Fact]
    public async Task Replay_leaves_both_databases_byte_identical_and_a_write_through_its_connection_throws()
    {
        WriteContent(ToolBody(Output));
        WriteCapture(ToolBody($"{Output}\nextra"));
        var before = Hashes();

        await ReplayAsync("--minimum", "300");

        Assert.Equal(before, Hashes());
        using var connection = ReplayReader.OpenReadOnly(Db("capture.db"));
        Assert.Equal(SqliteOpenMode.ReadOnly, new SqliteConnectionStringBuilder(connection.ConnectionString).Mode);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM capture_request";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(before, Hashes());
    }

    [Fact]
    public async Task A_replay_engine_pointed_at_a_missing_content_database_replays_and_leaves_no_file_behind()
    {
        var database = Db("content.db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Proxy:Compression:Database"] = database }).Build();
        using var engine = ReplayEngine.Create(300, null, null, null, configuration);

        var row = await engine.ReplayAsync(ToolBody(Output), TestContext.Current.CancellationToken);

        Assert.Equal(ReplayOutcome.Replayed, row.Outcome);
        Assert.False(File.Exists(database));
        Assert.Empty(Directory.GetFiles(_home.Path));
    }

    [Fact]
    public async Task A_row_that_is_not_json_and_a_row_with_no_tool_region_are_counted_in_their_own_denominators()
    {
        var noTool = Encoding.UTF8.GetBytes("""{"messages":[{"role":"user","content":"hello"}]}""");
        WriteCapture(Encoding.UTF8.GetBytes("this is not json"), noTool, ToolBody(Output));

        var result = await ReplayAsync("--minimum", "300");

        Assert.Equal(3, result.GetProperty("rowsRead").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsNotJson").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsNoToolRegion").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsReplayed").GetInt32());
    }

    [Fact]
    public async Task The_same_body_in_both_sources_is_counted_once_and_the_other_is_a_duplicate()
    {
        WriteContent(ToolBody(Output));
        WriteCapture(ToolBody(Output));

        var result = await ReplayAsync("--minimum", "300");

        Assert.Equal(2, result.GetProperty("rowsRead").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsDuplicate").GetInt32());
        Assert.Equal(1, Assert.Single(result.GetProperty("previews").EnumerateArray()).GetProperty("sends").GetInt32());
        Assert.Equal(1, result.GetProperty("captureRows").GetInt32());
    }

    [Fact]
    public async Task A_preview_sent_by_two_requests_is_summed_over_both_under_its_id()
    {
        WriteCapture(ToolBody(Output), ToolBody(Output).Concat(" "u8.ToArray()).ToArray());

        var result = await ReplayAsync("--minimum", "300", "--source", "capture");

        var preview = Assert.Single(result.GetProperty("previews").EnumerateArray());
        Assert.Equal(2, preview.GetProperty("sends").GetInt32());
        Assert.Equal(result.GetProperty("savedTokens").GetInt64(), preview.GetProperty("savedTokens").GetInt64());
        Assert.Equal(2, result.GetProperty("captureRows").GetInt32());
    }

    [Fact]
    public async Task A_body_the_reducers_shrink_but_that_sits_under_the_floor_has_no_preview()
    {
        WriteCapture(ToolBody(PrettyJson));

        var result = await ReplayAsync("--minimum", "3000");

        var kind = Assert.Single(result.GetProperty("kinds").EnumerateArray());
        Assert.Equal(1, result.GetProperty("rowsReplayed").GetInt32());
        Assert.Equal(0, result.GetProperty("rowsWithPreview").GetInt32());
        Assert.Empty(result.GetProperty("previews").EnumerateArray());
        Assert.Equal(0, kind.GetProperty("blocksPreviewed").GetInt32());
        Assert.Equal(2533, kind.GetProperty("reducerSavedTokens").GetInt64());
        Assert.Equal(0, kind.GetProperty("previewSavedTokens").GetInt64());
    }

    [Fact]
    public async Task A_whole_region_code_view_is_a_reducer_saving_and_not_a_preview()
    {
        WriteCapture(ReadBody("src/Example.cs", CodeFile));

        var result = await ReplayAsync("--minimum", "99999");

        var kind = Assert.Single(result.GetProperty("kinds").EnumerateArray());
        Assert.Equal("SourceCode", kind.GetProperty("kind").GetString());
        Assert.Equal(0, kind.GetProperty("blocksPreviewed").GetInt32());
        Assert.Empty(result.GetProperty("previews").EnumerateArray());
        Assert.Equal(0, result.GetProperty("rowsWithPreview").GetInt32());
        Assert.True(kind.GetProperty("reducerSavedTokens").GetInt64() > 1000);
        Assert.Equal(0, kind.GetProperty("previewSavedTokens").GetInt64());
    }

    [Fact]
    public async Task Each_kind_reports_its_blocks_and_its_reducer_and_preview_savings_separately()
    {
        // Pretty JSON: 11,492 characters = 3,976 tokens, which the reducers compact to 4,171 characters = 1,443 tokens, under the 1,500 floor.
        // Build log: 5,543 characters = 1,917 tokens, which no reducer shrinks and the preview takes to 748 tokens (budget share 0.4 of 1,917).
        WriteCapture(TwoToolBody(PrettyJson, Output));

        var result = await ReplayAsync("--minimum", "1500");

        var kinds = result.GetProperty("kinds").EnumerateArray().ToArray();
        Assert.Equal(2, kinds.Length);
        AssertKind(kinds[0], "Json", 1, 0, 3976, 2533, 0, 1443);
        AssertKind(kinds[1], "Prose", 1, 1, 1917, 0, 1169, 748);
        Assert.Equal(2533, result.GetProperty("reducerSavedTokens").GetInt64());
        Assert.Equal(1169, result.GetProperty("previewSavedTokens").GetInt64());
        Assert.Equal(3702, result.GetProperty("savedTokens").GetInt64());
        var preview = Assert.Single(result.GetProperty("previews").EnumerateArray());
        Assert.Equal(1169, preview.GetProperty("savedTokens").GetInt64());
    }

    private static void AssertKind(JsonElement kind, string name, int seen, int previewed, long original, long reducerSaved, long previewSaved, long forwarded)
    {
        Assert.Equal(name, kind.GetProperty("kind").GetString());
        Assert.Equal(seen, kind.GetProperty("blocksSeen").GetInt32());
        Assert.Equal(previewed, kind.GetProperty("blocksPreviewed").GetInt32());
        Assert.Equal(original, kind.GetProperty("originalTokens").GetInt64());
        Assert.Equal(reducerSaved, kind.GetProperty("reducerSavedTokens").GetInt64());
        Assert.Equal(previewSaved, kind.GetProperty("previewSavedTokens").GetInt64());
        Assert.Equal(forwarded, kind.GetProperty("forwardedTokens").GetInt64());
    }

    [Fact]
    public async Task A_sample_without_capture_rows_is_labelled_content_only_and_the_text_warns()
    {
        WriteContent(ToolBody(Output));
        var json = await ReplayAsync("--minimum", "300");
        var code = await new CliApplication(_fixture.Runtime).RunAsync(["replay", "--home", _home.Path, "--minimum", "300"], TestContext.Current.CancellationToken);

        Assert.Equal("content-only", json.GetProperty("coverage").GetString());
        Assert.Equal(0, code);
        Assert.Contains("coverage is content-only", _fixture.Console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sample_with_capture_rows_is_labelled_capture_and_the_text_does_not_warn()
    {
        WriteCapture(ToolBody(Output));
        var json = await ReplayAsync("--minimum", "300");
        var code = await new CliApplication(_fixture.Runtime).RunAsync(["replay", "--home", _home.Path, "--minimum", "300"], TestContext.Current.CancellationToken);

        Assert.Equal("capture", json.GetProperty("coverage").GetString());
        Assert.Equal(0, code);
        Assert.DoesNotContain("coverage is content-only", _fixture.Console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Since_keeps_the_rows_at_and_after_the_time_and_drops_the_ones_before()
    {
        WriteCaptureAt(Stamp.AddSeconds(-1), "api.anthropic.com", ToolBody($"{Output}\nbefore"));
        WriteCaptureAt(Stamp, "api.anthropic.com", ToolBody($"{Output}\nat"));
        WriteCaptureAt(Stamp.AddSeconds(1), "api.anthropic.com", ToolBody($"{Output}\nafter"));

        var result = await ReplayAsync("--minimum", "300", "--since", Stamp.ToString("O"));

        Assert.Equal(2, result.GetProperty("rowsRead").GetInt32());
    }

    [Fact]
    public async Task Source_content_reads_no_capture_rows()
    {
        WriteContent(ToolBody(Output));
        WriteCapture(ToolBody($"{Output}\nother"));

        var result = await ReplayAsync("--minimum", "300", "--source", "content");

        Assert.Equal(1, result.GetProperty("rowsRead").GetInt32());
        Assert.Equal(0, result.GetProperty("captureRows").GetInt32());
        Assert.Equal("content-only", result.GetProperty("coverage").GetString());
    }

    [Fact]
    public async Task A_zstd_capture_copy_of_a_content_body_is_one_duplicate_by_its_decoded_bytes()
    {
        using var compressor = new Compressor();
        WriteContent(ToolBody(Output));
        WriteCapture(compressor.Wrap(ToolBody(Output)).ToArray());

        var result = await ReplayAsync("--minimum", "300");

        Assert.Equal(2, result.GetProperty("rowsRead").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsDuplicate").GetInt32());
        Assert.Equal(0, result.GetProperty("rowsEncoded").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsReplayed").GetInt32());
    }

    [Fact]
    public async Task Encoded_bodies_are_left_out_unless_they_are_asked_for()
    {
        using var compressor = new Compressor();
        WriteCapture(compressor.Wrap(ToolBody(Output)).ToArray());

        var left = await ReplayAsync("--minimum", "300");
        var included = await ReplayAsync("--minimum", "300", "--include-encoded");

        Assert.Equal(1, left.GetProperty("rowsEncoded").GetInt32());
        Assert.Equal(0, left.GetProperty("rowsReplayed").GetInt32());
        Assert.Equal(0, left.GetProperty("savedTokens").GetInt64());
        Assert.Equal(0, included.GetProperty("rowsEncoded").GetInt32());
        Assert.Equal(1, included.GetProperty("rowsReplayed").GetInt32());
        Assert.Equal(1169, included.GetProperty("savedTokens").GetInt64());
    }

    [Fact]
    public async Task Rows_are_counted_per_host_and_a_content_row_has_no_host()
    {
        WriteContent(ToolBody(Output));
        WriteCaptureAt(Stamp, "api.openai.com", ToolBody($"{Output}\none"), ToolBody($"{Output}\ntwo"));

        var result = await ReplayAsync("--minimum", "300");

        var hosts = result.GetProperty("hosts").EnumerateArray().Select(host => (host.GetProperty("host").GetString(), host.GetProperty("rows").GetInt32())).ToArray();
        Assert.Equal([("api.openai.com", 2), ("unknown", 1)], hosts);
    }

    [Fact]
    public async Task A_numbered_read_is_counted_as_skipped_and_not_previewed()
    {
        WriteCapture(ToolBody(NumberedOutput));

        var result = await ReplayAsync("--minimum", "300", "--skip-under", "100000");

        Assert.Equal(1, result.GetProperty("blocksSkipped").GetInt32());
        Assert.Equal(1, Assert.Single(result.GetProperty("kinds").EnumerateArray()).GetProperty("blocksSkipped").GetInt32());
        Assert.Equal(0, result.GetProperty("rowsWithPreview").GetInt32());
        Assert.Empty(result.GetProperty("previews").EnumerateArray());
    }

    [Fact]
    public async Task With_the_skip_off_the_same_read_is_previewed_and_the_band_is_printed()
    {
        WriteCapture(ToolBody(NumberedOutput));

        var result = await ReplayAsync("--minimum", "300", "--skip-under", "0");

        Assert.Equal(0, result.GetProperty("blocksSkipped").GetInt32());
        Assert.Equal(0, result.GetProperty("skipUnder").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsWithPreview").GetInt32());
    }

    [Fact]
    public async Task A_numbered_read_under_the_minimum_is_not_counted_as_skipped()
    {
        WriteCapture(ToolBody(NumberedOutput));

        var result = await ReplayAsync("--minimum", "99999", "--skip-under", "100000");

        Assert.Equal(0, result.GetProperty("blocksSkipped").GetInt32());
    }

    [Fact]
    public async Task The_budget_share_option_reaches_the_pipeline_so_a_smaller_share_saves_more()
    {
        WriteCapture(ToolBody(Output));

        var standard = await ReplayAsync("--minimum", "300");
        var small = await ReplayAsync("--minimum", "300", "--budget-share", "0.1");

        Assert.Equal(1169, standard.GetProperty("savedTokens").GetInt64());
        Assert.True(small.GetProperty("savedTokens").GetInt64() > 1169);
    }

    [Fact]
    public async Task The_minimum_saving_option_reaches_the_pipeline_so_a_large_one_sends_the_reduced_text()
    {
        WriteCapture(ToolBody(Output));

        var standard = await ReplayAsync("--minimum", "300");
        var strict = await ReplayAsync("--minimum", "300", "--minimum-saving", "1170");

        Assert.Equal(1, standard.GetProperty("rowsWithPreview").GetInt32());
        Assert.Equal(0, strict.GetProperty("rowsWithPreview").GetInt32());
    }

    [Theory]
    [InlineData("--budget-share", "0")]
    [InlineData("--budget-share", "1.01")]
    [InlineData("--minimum-saving", "-1")]
    public async Task A_budget_share_outside_zero_to_one_or_a_negative_minimum_saving_is_refused(string option, string value)
    {
        var code = await new CliApplication(_fixture.Runtime).RunAsync(["replay", "--home", _home.Path, option, value], TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
    }

    [Theory]
    [InlineData("--budget-share", "1")]
    [InlineData("--minimum-saving", "0")]
    public async Task A_budget_share_of_one_and_a_zero_minimum_saving_are_accepted(string option, string value)
    {
        WriteCapture(ToolBody(Output));

        await ReplayAsync("--minimum", "300", option, value);
    }

    [Fact]
    public async Task A_negative_skip_band_is_refused()
    {
        var code = await new CliApplication(_fixture.Runtime).RunAsync(["replay", "--home", _home.Path, "--skip-under", "-1"], TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
    }

    [Fact]
    public async Task The_gain_request_count_covers_only_request_rows_in_the_window()
    {
        WriteCapture(ToolBody(Output));
        WriteGain(("request", Stamp.AddDays(-1)), ("request", Stamp), ("request", Stamp.AddSeconds(5)), ("failure", Stamp.AddSeconds(5)));

        var result = await ReplayAsync("--minimum", "300", "--since", Stamp.ToString("O"));

        Assert.Equal(2, result.GetProperty("gainRequests").GetInt64());
    }

    [Fact]
    public async Task The_engine_binds_its_compression_settings_from_configuration_like_the_proxy_host()
    {
        var body = ReadBody("src/Example.cs", CodeFile);
        using var defaults = ReplayEngine.Create(99999, null, null, null, new ConfigurationBuilder().Build());
        using var noViews = ReplayEngine.Create(99999, null, null, null, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Proxy:Compression:CodeViews"] = "false" }).Build());

        var withViews = Assert.Single((await defaults.ReplayAsync(body, TestContext.Current.CancellationToken)).Blocks);
        var withoutViews = Assert.Single((await noViews.ReplayAsync(body, TestContext.Current.CancellationToken)).Blocks);

        Assert.True(withViews.ReducerSaved > 1000);
        Assert.True(withoutViews.ReducerSaved < 100);
    }

    [Fact]
    public async Task The_text_output_prints_the_denominators_before_the_kind_table()
    {
        WriteCapture(ToolBody(Output));
        var code = await new CliApplication(_fixture.Runtime).RunAsync(["replay", "--home", _home.Path, "--minimum", "300"], TestContext.Current.CancellationToken);

        var text = _fixture.Console.Output;
        Assert.Equal(0, code);
        Assert.True(text.IndexOf("undecodable", StringComparison.Ordinal) < text.IndexOf("Reducers", StringComparison.Ordinal));
        Assert.Contains("gain requests", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_source_is_refused()
    {
        var code = await new CliApplication(_fixture.Runtime).RunAsync(["replay", "--home", _home.Path, "--source", "nowhere"], TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("source", _fixture.Error.ToString(), StringComparison.Ordinal);
    }
}
