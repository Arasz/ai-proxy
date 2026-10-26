using System.Net;
using System.Net.Http.Json;
using AiProxy.Core.Gain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Providers;
using Microsoft.AspNetCore.Http;
using static AiProxy.Tests.Unit.Content.Ccr.RetrievalLoopTests;

namespace AiProxy.Tests.Integration;

/// <summary>CCR through the real proxy against a streaming upstream: previews go up, retrievals are answered inside the request, and the client gets one message.</summary>
public sealed class CcrRelayTests : IDisposable
{
    private static readonly string Large = string.Join("\n", Enumerable.Range(1, 600).Select(index => $"step {index:D4}: compiled module m{index} into obj/m{index}.o"));

    private static readonly string Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Large)))[..16];

    private static readonly string Retrieve = JsonSerializer.Serialize(new { id = Id, mode = "lines", start = 2, end = 3 });

    private const string CodeFile =
        "using System;\nusing System.Collections.Generic;\n\n/// <summary>A type whose doc comment is long enough that the code view, hint included, is smaller than the file.</summary>\npublic sealed class Example\n{\n    public int Value => 1; // the value the model reads back exactly\n\n    /// <summary>A second member, so the file is over the compression minimum for source code.</summary>\n    public int Other => 2; // another comment the view drops\n\n    /// <summary>A third member with its own doc comment, also dropped from the view.</summary>\n    public int Third => 3;\n}\n";

    private const string PythonFile =
        "import os\nimport sys\nfrom typing import Any\n\n# A module comment long enough that the code view, hint included, is smaller than the file.\n\n\ndef value(item: Any) -> str:\n    \"\"\"Returns the item's text; the docstring stays in the view.\"\"\"\n    return os.fspath(item)  # the value the model reads back exactly\n\n\n# A second comment, so the file is over the compression minimum for source code.\ndef main() -> int:\n    # a third comment the view drops\n    print(value(sys.argv[0]))\n    return 0\n\n\n# Entry point; this comment goes too.\nif __name__ == \"__main__\":\n    sys.exit(main())\n";

    private static readonly string ViewId = CcrView.IdOf(CodeFile);

    private static readonly string RetrieveFile = JsonSerializer.Serialize(new { id = ViewId, mode = "whole" });

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-ccr-{Guid.CreateVersion7():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_stateless_retrieval_is_answered_inside_the_request_and_the_client_gets_one_message()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Sse.Message("msg_1", "tool_use", 4, Sse.Text("reading"), Sse.Tool("tu_r", CcrRetrieval.Name, Retrieve)),
            Sse.Message("msg_2", "end_turn", 6, Sse.Text("answer")));
        await using var proxy = await StartProxyAsync();

        using var response = await SendAsync(proxy, upstream, StatelessRequest());
        var client = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("reading", client, StringComparison.Ordinal);
        Assert.Contains("answer", client, StringComparison.Ordinal);
        Assert.DoesNotContain(CcrRetrieval.Name, client, StringComparison.Ordinal);
        Assert.Single(client.Split("event: message_start").Skip(1));
        Assert.Contains("\"output_tokens\":10", client, StringComparison.Ordinal);

        Assert.Equal(2, upstream.Requests.Count);
        var first = Json(upstream.Requests[0].Body);
        Assert.Equal(CcrRetrieval.Name, first.GetProperty("tools").EnumerateArray().Last().GetProperty("name").GetString());
        Assert.StartsWith(CcrPreview.Marker, first.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("content").GetString(), StringComparison.Ordinal);

        var followUp = Json(upstream.Requests[1].Body).GetProperty("messages");
        Assert.Equal(3, followUp.GetArrayLength());
        Assert.Equal("assistant", followUp[1].GetProperty("role").GetString());
        Assert.Equal("tu_r", followUp[1].GetProperty("content")[1].GetProperty("id").GetString());
        var answer = followUp[2].GetProperty("content")[0].GetProperty("content").GetString();
        Assert.Contains("2\tstep 0002", answer, StringComparison.Ordinal);
        Assert.Contains("3\tstep 0003", answer, StringComparison.Ordinal);
        Assert.True(client.TrimEnd().EndsWith("data: {\"type\":\"message_stop\"}", StringComparison.Ordinal), "message_stop must be the last event");
        foreach (var sent in upstream.Requests)
        {
            Assert.Equal("Bearer secret", sent.Headers["Authorization"]);
            Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
            Assert.Equal("context-1", sent.Headers["anthropic-beta"]);
            Assert.DoesNotContain(sent.Headers.Keys, key => key.Equals("X-Ai-Proxy-Project", StringComparison.OrdinalIgnoreCase));
        }
        using var api = new HttpClient { BaseAddress = proxy.BaseAddress };
        var gain = await api.GetFromJsonAsync<GainReport>("/gain", TestContext.Current.CancellationToken);
        Assert.Equal(1, gain!.Summary.Requests);
        Assert.Equal(1, gain.Summary.Recalls);
        Assert.Equal(Id, Assert.Single(gain.Recalls).PreviewId);
        Assert.False(gain.Measurement.NetSavingsAvailable);
        Assert.Equal(Encoding.UTF8.GetByteCount(upstream.Requests[0].Body), gain.Summary.ForwardedBytes);
    }

    [Fact]
    public async Task A_thread_retrieval_continues_upstream_and_the_next_continue_is_pointed_at_the_hidden_turn()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Sse.Message("msg_A", "tool_use", 1, Sse.Tool("tu_r", CcrRetrieval.Name, Retrieve), Sse.Tool("tu_c", "Read", "{}")),
            Sse.Message("msg_B", "end_turn", 1, Sse.Text("answer")),
            Sse.Message("msg_C", "end_turn", 1, Sse.Text("next answer")));
        await using var proxy = await StartProxyAsync();

        using (var created = await SendAsync(proxy, upstream, CreateRequest()))
        {
            var client = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("msg_A", client, StringComparison.Ordinal);
            Assert.DoesNotContain("tu_c", client, StringComparison.Ordinal);
        }

        var followUp = Json(upstream.Requests[1].Body);
        Assert.Equal("continue", followUp.GetProperty("thread").GetProperty("type").GetString());
        Assert.Equal("msg_A", followUp.GetProperty("thread").GetProperty("previous_message_id").GetString());
        Assert.False(followUp.TryGetProperty("tools", out _));
        var results = Assert.Single(followUp.GetProperty("messages").EnumerateArray()).GetProperty("content");
        Assert.Equal(["tu_r", "tu_c"], results.EnumerateArray().Select(result => result.GetProperty("tool_use_id").GetString()));
        Assert.True(results[1].GetProperty("is_error").GetBoolean());

        using (var continued = await SendAsync(proxy, upstream, """{"model":"m","stream":true,"messages":[{"role":"user","content":"next"}],"thread":{"type":"continue","previous_message_id":"msg_A"}}"""))
        {
            Assert.Contains("next answer", await continued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal("msg_B", Json(upstream.Requests[2].Body).GetProperty("thread").GetProperty("previous_message_id").GetString());
    }

    [Fact]
    public async Task A_continue_of_a_thread_the_ledger_does_not_know_is_forwarded_untouched()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Sse.Message("msg_X", "end_turn", 1, Sse.Text("plain")));
        await using var proxy = await StartProxyAsync();
        var body = """{"model":"m","stream":true,"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":""" + JsonSerializer.Serialize(Large) + """}]}],"thread":{"type":"continue","previous_message_id":"msg_unknown"}}""";

        using var response = await SendAsync(proxy, upstream, body);

        Assert.Contains("plain", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain(CcrPreview.Marker, Assert.Single(upstream.Requests).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_captured_relayed_exchange_keeps_the_merged_usage_and_the_round_timings()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Sse.Message("msg_1", "tool_use", 4, Sse.Tool("tu_r", CcrRetrieval.Name, Retrieve)),
            Sse.Message("msg_2", "end_turn", 6, Sse.Text("answer")));
        await using var proxy = await StartProxyAsync(new KeyValuePair<string, string>("Proxy:Capture", "true"));

        using (await SendAsync(proxy, upstream, StatelessRequest()))
        {
        }

        var row = await ReadResponseRowAsync(Path.Combine(proxy.DataDirectory, Storage.SqliteCaptureStore.FileName));
        Assert.Equal(200, row.Status);
        Assert.Equal(10, row.OutputTokens);
        Assert.Contains("ccr.round-1", row.Stages, StringComparison.Ordinal);
        Assert.Contains("ccr.round-2", row.Stages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_cut_off_before_message_stop_ends_in_an_error_event_and_no_message_stop()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var whole = Sse.Message("msg_1", "end_turn", 1, Sse.Text("partial"));
        upstream.Handler = Script(whole[..whole.IndexOf("event: message_delta", StringComparison.Ordinal)]);
        await using var proxy = await StartProxyAsync();

        using var response = await SendAsync(proxy, upstream, StatelessRequest());
        var client = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("partial", client, StringComparison.Ordinal);
        Assert.Contains("event: error", client, StringComparison.Ordinal);
        Assert.Contains("ended before the message did", client, StringComparison.Ordinal);
        Assert.DoesNotContain("message_stop", client, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_upstream_error_reaches_the_client_with_its_status_body_and_retry_header()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static async (_, context) =>
        {
            context.Response.StatusCode = 429;
            context.Response.Headers["retry-after"] = "7";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""");
        };
        await using var proxy = await StartProxyAsync();

        using var response = await SendAsync(proxy, upstream, StatelessRequest());

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal("7", response.Headers.GetValues("retry-after").Single());
        Assert.Contains("rate_limit_error", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_csharp_file_goes_up_as_its_view_and_a_retrieval_gets_the_exact_file_back()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Sse.Message("msg_1", "tool_use", 1, Sse.Tool("tu_r", CcrRetrieval.Name, RetrieveFile)),
            Sse.Message("msg_2", "end_turn", 1, Sse.Text("answer")));
        await using var proxy = await StartProxyAsync();

        using var response = await SendAsync(proxy, upstream, CodeRequest().ToJsonString());

        Assert.Contains("answer", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var view = ToolResultOf(upstream.Requests[0].Body);
        Assert.EndsWith(CcrView.Hint(ViewId), view, StringComparison.Ordinal);
        Assert.DoesNotContain("doc comment", view, StringComparison.Ordinal);
        Assert.EndsWith("\n" + CodeFile, ToolResultOf(upstream.Requests[1].Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_python_file_goes_up_as_its_view_and_a_retrieval_gets_the_exact_file_back()
    {
        var id = CcrView.IdOf(PythonFile);
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Sse.Message("msg_1", "tool_use", 1, Sse.Tool("tu_r", CcrRetrieval.Name, JsonSerializer.Serialize(new { id, mode = "whole" }))),
            Sse.Message("msg_2", "end_turn", 1, Sse.Text("answer")));
        await using var proxy = await StartProxyAsync();

        using (await SendAsync(proxy, upstream, CodeRequest("src/tool.py", PythonFile).ToJsonString()))
        {
        }

        var view = ToolResultOf(upstream.Requests[0].Body);
        Assert.EndsWith(CcrView.Hint(id), view, StringComparison.Ordinal);
        Assert.DoesNotContain("module comment", view, StringComparison.Ordinal);
        Assert.EndsWith("\n" + PythonFile, ToolResultOf(upstream.Requests[1].Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_typescript_file_goes_up_as_its_view_and_a_retrieval_gets_the_exact_file_back()
    {
        const string typeScript =
            "import { readFile } from \"node:fs/promises\";\nimport type { Stats } from \"node:fs\";\n\n/** A doc comment long enough that the code view, hint included, is smaller than the file. */\nexport interface Entry {\n    name: string; // the name the model reads back exactly\n    stats?: Stats;\n}\n\n// A second comment, so the file is over the compression minimum for source code.\nexport async function load(entry: Entry): Promise<string> {\n    /* a block comment the view drops */\n    return readFile(entry.name, \"utf8\");\n}\n\n// A last comment, also dropped from the view.\nexport const empty: Entry = { name: \"\" };\n";
        var id = CcrView.IdOf(typeScript);
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Sse.Message("msg_1", "tool_use", 1, Sse.Tool("tu_r", CcrRetrieval.Name, JsonSerializer.Serialize(new { id, mode = "whole" }))),
            Sse.Message("msg_2", "end_turn", 1, Sse.Text("answer")));
        await using var proxy = await StartProxyAsync();

        using (await SendAsync(proxy, upstream, CodeRequest("src/load.ts", typeScript).ToJsonString()))
        {
        }

        var view = ToolResultOf(upstream.Requests[0].Body);
        Assert.EndsWith(CcrView.Hint(id), view, StringComparison.Ordinal);
        Assert.DoesNotContain("doc comment", view, StringComparison.Ordinal);
        Assert.EndsWith("\n" + typeScript, ToolResultOf(upstream.Requests[1].Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_continue_retrieves_a_view_from_an_earlier_turn_through_the_store()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Sse.Message("msg_A", "end_turn", 1, Sse.Text("seen")),
            Sse.Message("msg_C", "tool_use", 1, Sse.Tool("tu_r", CcrRetrieval.Name, RetrieveFile)),
            Sse.Message("msg_D", "end_turn", 1, Sse.Text("done")));
        await using var proxy = await StartProxyAsync();

        using (await SendAsync(proxy, upstream, CodeRequest().Also(request => request["thread"] = new JsonObject { ["type"] = "create" }).ToJsonString()))
        {
        }

        using var continued = await SendAsync(proxy, upstream, """{"model":"m","stream":true,"messages":[{"role":"user","content":"next"}],"thread":{"type":"continue","previous_message_id":"msg_A"}}""");

        Assert.Contains("done", await continued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(3, upstream.Requests.Count);
        Assert.EndsWith("\n" + CodeFile, ToolResultOf(upstream.Requests[2].Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_code_views_off_a_read_csharp_file_goes_up_without_a_view()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Sse.Message("msg_1", "end_turn", 1, Sse.Text("answer")));
        await using var proxy = await StartProxyAsync(new KeyValuePair<string, string>("Proxy:Compression:CodeViews", "false"));

        using (await SendAsync(proxy, upstream, CodeRequest().ToJsonString()))
        {
        }

        Assert.DoesNotContain(CcrView.Marker, ToolResultOf(Assert.Single(upstream.Requests).Body), StringComparison.Ordinal);
    }

    private static string ToolResultOf(string body) =>
        Json(body).GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0].GetProperty("content").GetString() ?? string.Empty;

    private static JsonObject CodeRequest() => CodeRequest("src/Example.cs", CodeFile);

    private static JsonObject CodeRequest(string path, string text) => new()
    {
        ["model"] = "m",
        ["max_tokens"] = 5,
        ["stream"] = true,
        ["tools"] = new JsonArray(new JsonObject { ["name"] = "Read", ["input_schema"] = new JsonObject { ["type"] = "object" } }),
        ["messages"] = new JsonArray(
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "t0", ["name"] = "Read", ["input"] = new JsonObject { ["file_path"] = path } }),
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t0", ["content"] = text }),
            }),
    };

    private static string StatelessRequest() => Request().ToJsonString();

    private static string CreateRequest() => Request().Also(request => request["thread"] = new JsonObject { ["type"] = "create" }).ToJsonString();

    private static JsonObject Request() => new()
    {
        ["model"] = "m",
        ["max_tokens"] = 5,
        ["stream"] = true,
        ["tools"] = new JsonArray(new JsonObject { ["name"] = "Read", ["input_schema"] = new JsonObject { ["type"] = "object" } }),
        ["messages"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t0", ["content"] = Large }),
        }),
    };

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static Func<FakeUpstream, HttpContext, Task> Script(params string[] streams)
    {
        var next = 0;
        return async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(streams[Math.Min(Interlocked.Increment(ref next) - 1, streams.Length - 1)]);
        };
    }

    private Task<ProxyHost> StartProxyAsync(params KeyValuePair<string, string>[] extra) => ProxyHost.StartAsync(
        new Dictionary<string, string>(extra)
        {
            ["Proxy:Compression:Enabled"] = "true",
            ["Proxy:Compression:Database"] = _databasePath,
        },
        providerHosts: new LoopbackProviderHosts(Dialect.Anthropic));

    private static async Task<ResponseRow> ReadResponseRowAsync(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status, output_tokens, stages FROM capture_response;";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            if (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                return new ResponseRow(reader.GetInt32(0), reader.IsDBNull(1) ? -1 : reader.GetInt64(1), reader.GetString(2));
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException("No capture_response row was written for the relayed exchange.");
    }

    private sealed record ResponseRow(int Status, long OutputTokens, string Stages);

    private static async Task<HttpResponseMessage> SendAsync(ProxyHost proxy, FakeUpstream upstream, string body)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(upstream.BaseAddress, "/v1/messages?beta=true"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        message.Headers.TryAddWithoutValidation("anthropic-beta", "context-1");
        message.Headers.TryAddWithoutValidation(GainApi.ProjectHeader, "/repo/project");
        var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        await response.Content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
        return response;
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject node, Action<JsonObject> change)
    {
        change(node);
        return node;
    }
}
