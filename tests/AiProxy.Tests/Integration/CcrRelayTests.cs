using System.Net;
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

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-ccr-{Guid.NewGuid():N}.db");

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
        var first = JsonNode.Parse(upstream.Requests[0].Body)!;
        Assert.Equal(CcrRetrieval.Name, first["tools"]!.AsArray()[^1]!["name"]!.GetValue<string>());
        Assert.StartsWith(CcrPreview.Marker, first["messages"]![0]!["content"]![0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        var followUp = JsonNode.Parse(upstream.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal(3, followUp.Count);
        Assert.Equal("assistant", followUp[1]!["role"]!.GetValue<string>());
        Assert.Equal("tu_r", followUp[1]!["content"]![1]!["id"]!.GetValue<string>());
        var answer = followUp[2]!["content"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("2\tstep 0002", answer, StringComparison.Ordinal);
        Assert.Contains("3\tstep 0003", answer, StringComparison.Ordinal);
        Assert.Equal("Bearer secret", upstream.Requests[1].Headers["Authorization"]);
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

        var followUp = JsonNode.Parse(upstream.Requests[1].Body)!;
        Assert.Equal("continue", followUp["thread"]!["type"]!.GetValue<string>());
        Assert.Equal("msg_A", followUp["thread"]!["previous_message_id"]!.GetValue<string>());
        Assert.Null(followUp["tools"]);
        var results = followUp["messages"]!.AsArray().Single()!["content"]!.AsArray();
        Assert.Equal(["tu_r", "tu_c"], results.Select(result => result!["tool_use_id"]!.GetValue<string>()));
        Assert.True(results[1]!["is_error"]!.GetValue<bool>());

        using (var continued = await SendAsync(proxy, upstream, """{"model":"m","stream":true,"messages":[{"role":"user","content":"next"}],"thread":{"type":"continue","previous_message_id":"msg_A"}}"""))
        {
            Assert.Contains("next answer", await continued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var next = JsonNode.Parse(upstream.Requests[2].Body)!;
        Assert.Equal("msg_B", next["thread"]!["previous_message_id"]!.GetValue<string>());
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

        var row = await ReadResponseRowAsync(Path.Combine(proxy.DataDirectory, AiProxy.Storage.SqliteCaptureStore.FileName));
        Assert.Equal(200, row.Status);
        Assert.Equal(10, row.OutputTokens);
        Assert.Contains("ccr.round-1", row.Stages, StringComparison.Ordinal);
        Assert.Contains("ccr.round-2", row.Stages, StringComparison.Ordinal);
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
