using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiProxy.Cli;
using AiProxy.Storage;
using AiProxy.Tests.Integration;
using AiProxy.Tests.Unit.Cli;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace AiProxy.Tests.Unit.Replay;

/// <summary>The replay command reads stored bodies through the real pipeline and never changes the databases.</summary>
public sealed class ReplayCommandTests : IDisposable
{
    private static readonly string Output = string.Join("\n", Enumerable.Range(1, 120).Select(index => $"step {index:D4}: compiled module m{index} into obj/m{index}.o"));
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

    private static byte[] ToolBody(string output)
    {
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "t1", content = output } } } }
        }));
    }

    private string Db(string file)
    {
        return Path.Combine(_home.Path, file);
    }

    private void WriteContent(params byte[][] originals)
    {
        using var connection = Create("content.db", "CREATE TABLE content_request (request_id TEXT PRIMARY KEY, created_at TEXT NOT NULL, original TEXT NOT NULL, compressed TEXT NOT NULL, ratio REAL NOT NULL, path TEXT NOT NULL, chunk_count INTEGER NOT NULL)");
        foreach (var original in originals)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO content_request VALUES (@Id, @At, @Original, '', 1, '', 0)";
            command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("@At", Stamp.ToString("O"));
            command.Parameters.AddWithValue("@Original", Encoding.UTF8.GetString(original));
            command.ExecuteNonQuery();
        }
    }

    private void WriteCapture(params byte[][] bodies)
    {
        using var connection = Create("capture.db", "CREATE TABLE capture_request (request_id TEXT PRIMARY KEY, captured_at TEXT NOT NULL, provider TEXT NOT NULL, host TEXT NOT NULL, path TEXT NOT NULL, body BLOB NOT NULL)");
        foreach (var body in bodies)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO capture_request VALUES (@Id, @At, 'anthropic', 'api.anthropic.com', '/v1/messages', @Body)";
            command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("@At", Stamp.ToString("O"));
            command.Parameters.AddWithValue("@Body", body);
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

    private string Hashes()
    {
        return string.Join(",", new[] { "content.db", "capture.db" }.Select(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Db(file))))));
    }

    [Fact]
    public async Task The_minimum_option_reaches_the_pipeline_so_the_same_body_previews_only_when_it_is_low_enough()
    {
        WriteCapture(ToolBody(Output));

        var high = await ReplayAsync("--minimum", "3000");
        var low = await ReplayAsync("--minimum", "300");

        Assert.Equal(1, high.GetProperty("rowsReplayed").GetInt32());
        Assert.Equal(0, high.GetProperty("rowsWithPreview").GetInt32());
        Assert.Equal(1, low.GetProperty("rowsWithPreview").GetInt32());
        Assert.True(low.GetProperty("savedTokens").GetInt64() > 0);
        Assert.Equal(1, Assert.Single(low.GetProperty("previews").EnumerateArray()).GetProperty("sends").GetInt32());
    }

    [Fact]
    public async Task A_corrupt_zstd_capture_row_is_counted_undecodable_and_not_as_a_row_without_saving()
    {
        var corrupt = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3, 4, 5, 6, 7, 8 };
        WriteCapture(corrupt, ToolBody(Output));

        var result = await ReplayAsync("--minimum", "300");

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

        var result = await ReplayAsync("--minimum", "300");

        Assert.Equal(0, result.GetProperty("rowsUndecodable").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsWithPreview").GetInt32());
    }

    [Fact]
    public async Task Replay_leaves_both_databases_byte_identical_and_a_write_through_its_connection_throws()
    {
        WriteContent(ToolBody(Output));
        WriteCapture(ToolBody(Output + "\nextra"));
        var before = Hashes();

        await ReplayAsync("--minimum", "300");

        Assert.Equal(before, Hashes());
        using var connection = ReplayReader.OpenReadOnly(Db("capture.db"));
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM capture_request";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(before, Hashes());
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
    public async Task The_text_output_prints_the_denominators_before_the_kind_table()
    {
        WriteCapture(ToolBody(Output));
        var code = await new CliApplication(_fixture.Runtime).RunAsync(["replay", "--home", _home.Path, "--minimum", "300"], TestContext.Current.CancellationToken);

        var text = _fixture.Console.Output;
        Assert.Equal(0, code);
        Assert.True(text.IndexOf("undecodable", StringComparison.Ordinal) < text.IndexOf("Saved", StringComparison.Ordinal));
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
