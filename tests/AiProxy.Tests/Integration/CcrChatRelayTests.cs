using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Ccr.Loop;
using AiProxy.Core.Gain;
using AiProxy.Core.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using static AiProxy.Tests.Unit.Content.Ccr.RetrievalLoopTests;

namespace AiProxy.Tests.Integration;

/// <summary>CCR for streamed Chat Completions through the real proxy: a retrieval is answered inside the request and the client gets one chunk stream and one <c>[DONE]</c>.</summary>
public sealed class CcrChatRelayTests : IDisposable
{
    private static readonly string Large = string.Join("\n", Enumerable.Range(1, 600).Select(index => $"step {index:D4}: compiled module m{index} into obj/m{index}.o"));

    private static readonly string Id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Large)))[..16];

    private static readonly string Retrieve = JsonSerializer.Serialize(new { id = Id, mode = "lines", start = 2, end = 3 });

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-ccr-chat-{Guid.CreateVersion7():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            File.Delete(file);
        }
    }

    private static string Request(string extra = "", string model = "deepseek/deepseek-v4.1-flash-1017")
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read", ["parameters"] = new JsonObject { ["type"] = "object" } } }),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "go" },
                new JsonObject
                {
                    ["role"] = "assistant", ["content"] = null,
                    ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "t0", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read", ["arguments"] = "{}" } })
                },
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = "t0", ["content"] = Large })
        };
        if (extra.Length > 0)
        {
            request["stream_options"] = JsonNode.Parse(extra);
        }

        return request.ToJsonString();
    }

    private static string Round(string text, JsonObject? tools, string finish, int tokens, bool preamble = false)
    {
        var chunks = new List<string> { ChatSse.Chunk("gen-1", new JsonObject { ["role"] = "assistant", ["content"] = "" }, null, null) };
        if (preamble)
        {
            chunks.Insert(0, ": OPENROUTER PROCESSING\n\n");
        }

        if (text.Length > 0)
        {
            chunks.Add(ChatSse.Chunk("gen-1", ChatSse.Text(text), null, null));
        }

        if (tools is not null)
        {
            chunks.Add(ChatSse.Chunk("gen-1", tools, null, null));
        }

        chunks.Add(ChatSse.Chunk("gen-1", new JsonObject(), finish, null));
        chunks.Add(ChatSse.Chunk("gen-1", null, null, ChatSse.Usage(tokens)));
        chunks.Add("data: [DONE]\n\n");
        return string.Concat(chunks);
    }

    private static JsonObject Calls(params (int Index, string Id, string Name, string Arguments)[] calls)
    {
        return new JsonObject
        {
            ["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
            {
                ["index"] = call.Index, ["id"] = call.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
            }).ToArray())
        };
    }

    private static Func<FakeUpstream, HttpContext, Task> Script(params string[] streams)
    {
        var next = 0;
        return async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(streams[Math.Min(Interlocked.Increment(ref next) - 1, streams.Length - 1)]);
        };
    }

    private Task<ProxyHost> StartProxyAsync(bool chatHost = true, RecordingLoggerProvider? logs = null, string? keepAlive = null, TimeProvider? time = null)
    {
        var settings = new Dictionary<string, string> { ["Proxy:Compression:Enabled"] = "true", ["Proxy:Compression:Database"] = _databasePath };
        if (keepAlive is not null)
        {
            settings["Proxy:Ccr:KeepAliveInterval"] = keepAlive;
        }

        return ProxyHost.StartAsync(settings, providerHosts: new LoopbackProviderHosts(Dialect.OpenAi, chatHost), logging: logs, time: time);
    }

    private static JsonNode[] ChunksOf(string client)
    {
        return client.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(static block => block.StartsWith("data: ", StringComparison.Ordinal))
            .Select(static block => block["data: ".Length..]).Where(static data => data != "[DONE]").Select(Json).ToArray();
    }

    private static string ArgumentsOnly(int index, string fragment)
    {
        return ChatSse.Chunk("gen-1", new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = index, ["function"] = new JsonObject { ["arguments"] = fragment } }) }, null, null);
    }

    private static string Finished(params string[] chunks)
    {
        return ChatSse.Chunk("gen-1", new JsonObject { ["role"] = "assistant", ["content"] = "" }, null, null) + string.Concat(chunks)
               + ChatSse.Chunk("gen-1", null, null, ChatSse.Usage(4)) + "data: [DONE]\n\n";
    }

    private static async Task<string> SendAsync(ProxyHost proxy, FakeUpstream upstream, string body)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(upstream.BaseAddress, "/api/v1/chat/completions"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        message.Headers.TryAddWithoutValidation(GainApi.ProjectHeader, "/repo/project");
        using var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static JsonNode Json(string text)
    {
        return JsonNode.Parse(text)!;
    }

    private static int Count(string text, string needle)
    {
        return text.Split(needle).Length - 1;
    }

    [Fact]
    public async Task A_retrieval_is_answered_inside_the_request_and_the_client_gets_one_stream_with_one_done()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Round("reading", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), "tool_calls", 4),
            Round("answer", null, "stop", 6));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Contains("reading", client, StringComparison.Ordinal);
        Assert.Contains("answer", client, StringComparison.Ordinal);
        Assert.DoesNotContain(CcrRetrieval.Name, client, StringComparison.Ordinal);
        Assert.DoesNotContain("call_r", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "[DONE]"));
        Assert.EndsWith("data: [DONE]\n\n", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "\"role\":\"assistant\""));
        Assert.Contains("\"finish_reason\":\"stop\"", client, StringComparison.Ordinal);

        Assert.Equal(2, upstream.Requests.Count);
        var first = Json(upstream.Requests[0].Body);
        var last = first["tools"]!.AsArray()[^1]!;
        Assert.Equal("function", last["type"]!.GetValue<string>());
        Assert.Equal(CcrRetrieval.Name, last["function"]!["name"]!.GetValue<string>());
        Assert.Contains(CcrPreview.Marker, first["messages"]![2]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(first["stream_options"]!["include_usage"]!.GetValue<bool>());

        var messages = Json(upstream.Requests[1].Body)["messages"]!.AsArray();
        Assert.Equal(5, messages.Count);
        Assert.Equal("call_r", messages[3]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[4]!["role"]!.GetValue<string>());
        Assert.Equal("call_r", messages[4]!["tool_call_id"]!.GetValue<string>());
        Assert.Contains("2\tstep 0002", messages[4]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Bearer secret", upstream.Requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task A_proxy_retrieve_call_mixed_with_a_client_call_is_answered_and_the_client_call_is_deferred_not_released()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Round("", Calls((0, "call_r", CcrRetrieval.Name, Retrieve), (1, "call_c", "read", "{\"path\":\"a\"}")), "tool_calls", 4),
            Round("answer", null, "stop", 6));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.DoesNotContain("call_c", client, StringComparison.Ordinal);
        Assert.DoesNotContain("tool_calls", client, StringComparison.Ordinal);
        var messages = Json(upstream.Requests[1].Body)["messages"]!.AsArray();
        Assert.Equal(["call_r", "call_c"], messages[3]!["tool_calls"]!.AsArray().Select(static call => call!["id"]!.GetValue<string>()));
        Assert.Equal(["call_r", "call_c"], messages.Skip(4).Select(static tool => tool!["tool_call_id"]!.GetValue<string>()));
        Assert.Equal(RetrievalLoop.DeferredCall, messages[5]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_client_call_split_over_deltas_is_released_whole_and_numbered_from_zero()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var pieces = new[] { "{\"pa", "th\":\"", "a.cs\"}" };
        var split = string.Concat(
            ChatSse.Chunk("gen-2", new JsonObject { ["role"] = "assistant" }, null, null),
            ChatSse.Chunk("gen-2", Calls((2, "call_c", "read", "")), null, null),
            string.Concat(pieces.Select(piece => ChatSse.Chunk("gen-2", new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 2, ["function"] = new JsonObject { ["arguments"] = piece } }) }, null, null))),
            ChatSse.Chunk("gen-2", new JsonObject(), "tool_calls", null),
            ChatSse.Chunk("gen-2", null, null, ChatSse.Usage(5)),
            "data: [DONE]\n\n");
        upstream.Handler = Script(Round("", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), "tool_calls", 4), split);
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        var chunks = client.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(static line => line["data: ".Length..]).Where(static data => data != "[DONE]").Select(Json).ToArray();
        var call = Assert.Single(chunks, static chunk => chunk["choices"]!.AsArray().Count > 0 && chunk["choices"]![0]!["delta"]!["tool_calls"] is not null)["choices"]![0]!["delta"]!["tool_calls"]![0]!;
        Assert.Equal(0, call["index"]!.GetValue<int>());
        Assert.Equal("call_c", call["id"]!.GetValue<string>());
        Assert.Equal("{\"path\":\"a.cs\"}", call["function"]!["arguments"]!.GetValue<string>());
        Assert.Contains(chunks, static chunk => chunk["choices"]!.AsArray().Count > 0 && chunk["choices"]![0]!["finish_reason"]?.GetValue<string>() == "tool_calls");
        Assert.All(chunks, chunk => Assert.Equal("gen-1", chunk["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Usage_is_merged_over_the_rounds_and_sent_only_when_the_client_asked_for_it()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Round("a", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), "tool_calls", 4), Round("b", null, "stop", 6));
        await using var proxy = await StartProxyAsync();

        var asked = await SendAsync(proxy, upstream, Request("{\"include_usage\":true}"));
        var usage = Assert.Single(asked.Split("\n\n", StringSplitOptions.RemoveEmptyEntries), static chunk => chunk.Contains("\"usage\"", StringComparison.Ordinal));
        Assert.Equal(10, Json(usage["data: ".Length..])["usage"]!["completion_tokens"]!.GetValue<int>());
        Assert.True(Json(upstream.Requests[0].Body)["stream_options"]!["include_usage"]!.GetValue<bool>());

        var plain = await SendAsync(proxy, upstream, Request());
        Assert.DoesNotContain("usage", plain, StringComparison.Ordinal);
        Assert.Contains("\"b\"", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comment_lines_from_the_upstream_are_accepted()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Round("plain", null, "stop", 3, preamble: true));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Contains("plain", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "[DONE]"));
        Assert.DoesNotContain("OPENROUTER", client, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_in_stream_error_chunk_reaches_the_client_once_with_one_done()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            ChatSse.Chunk("gen-1", ChatSse.Text("part"), null, null) + "data: {\"error\":{\"message\":\"upstream boom\",\"code\":500}}\n\ndata: [DONE]\n\n");
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Contains("part", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "upstream boom"));
        Assert.Equal(1, Count(client, "[DONE]"));
        Assert.EndsWith("data: [DONE]\n\n", client, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_follow_up_becomes_an_error_chunk_then_done()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var calls = 0;
        upstream.Handler = async (_, context) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(Round("", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), "tool_calls", 4));
                return;
            }

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":{"message":"no good","code":400}}""");
        };
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Contains("no good", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "[DONE]"));
        Assert.EndsWith("data: [DONE]\n\n", client, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_upstream_gets_a_chat_shaped_error_body()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync();
        var address = upstream.BaseAddress;
        await upstream.DisposeAsync();

        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        using var response = await client.PostAsync(new Uri(address, "/api/v1/chat/completions"), new StringContent(Request(), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var body = Json(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.NotNull(body["error"]!["code"]);
        Assert.Null(body["type"]);
    }

    [Fact]
    public async Task A_host_without_chat_ccr_or_an_unknown_model_is_forwarded_untouched()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Round("plain", null, "stop", 3), Round("plain", null, "stop", 3));
        await using (var proxy = await StartProxyAsync(chatHost: false))
        {
            await SendAsync(proxy, upstream, Request());
        }

        await using (var proxy = await StartProxyAsync())
        {
            await SendAsync(proxy, upstream, Request(model: "openrouter/auto"));
        }

        Assert.All(upstream.Requests, request => Assert.DoesNotContain(CcrRetrieval.Name, request.Body, StringComparison.Ordinal));
        Assert.All(upstream.Requests, request => Assert.DoesNotContain("stream_options", request.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_follow_up_and_recall_rows_carry_the_request_host()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Round("a", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), "tool_calls", 4), Round("b", null, "stop", 6));
        await using var proxy = await StartProxyAsync();

        await SendAsync(proxy, upstream, Request());

        using var api = proxy.CreateApiClient();
        var report = (await api.GetFromJsonAsync<GainReport>("/gain", TestContext.Current.CancellationToken))!;
        Assert.Equal(1, report.Summary.FollowUps);
        Assert.Equal(1, report.Summary.Recalls);
        Assert.Equal([GainHarness.Of("openai", "127.0.0.1")], report.Harnesses!.Select(static row => row.Name));
    }

    [Fact]
    public async Task Text_and_a_tool_calls_finish_in_one_chunk_never_show_the_client_a_mid_stream_finish()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var both = new JsonObject { ["content"] = "reading", ["tool_calls"] = Calls((0, "call_r", CcrRetrieval.Name, Retrieve))["tool_calls"]!.DeepClone() };
        upstream.Handler = Script(Finished(ChatSse.Chunk("gen-1", both, "tool_calls", null)), Round("answer", null, "stop", 6));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        var finishes = ChunksOf(client).SelectMany(static chunk => chunk["choices"]!.AsArray()).Select(static choice => choice!["finish_reason"]?.GetValue<string>()).Where(static finish => finish is not null).ToArray();
        Assert.Equal(["stop"], finishes);
        Assert.Contains("reading", client, StringComparison.Ordinal);
        Assert.Contains("answer", client, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_up_that_errors_mid_stream_gives_the_client_one_error_and_one_done()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(
            Round("", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), "tool_calls", 4),
            ChatSse.Chunk("gen-2", ChatSse.Text("part"), null, null) + "data: {\"error\":{\"message\":\"late boom\",\"code\":500}}\n\ndata: [DONE]\n\n");
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Contains("part", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "late boom"));
        Assert.Equal(1, Count(client, "[DONE]"));
        Assert.EndsWith("data: [DONE]\n\n", client, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stop_with_no_tools_is_released_after_one_upstream_request()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Round("hello", null, "stop", 3));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Single(upstream.Requests);
        Assert.Contains("hello", client, StringComparison.Ordinal);
        Assert.Contains("\"finish_reason\":\"stop\"", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "[DONE]"));
    }

    [Fact]
    public async Task Parallel_calls_interleaved_on_two_indexes_are_joined_and_each_gets_a_tool_reply()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var half = Retrieve.Length / 2;
        upstream.Handler = Script(
            Finished(
                ChatSse.Chunk("gen-1", Calls((0, "call_r", CcrRetrieval.Name, "")), null, null),
                ChatSse.Chunk("gen-1", Calls((1, "call_c", "read", "{\"p\":")), null, null),
                ArgumentsOnly(0, Retrieve[..half]),
                ArgumentsOnly(1, "1}"),
                ArgumentsOnly(0, Retrieve[half..]),
                ChatSse.Chunk("gen-1", new JsonObject(), "tool_calls", null)),
            Round("answer", null, "stop", 6));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Contains("answer", client, StringComparison.Ordinal);
        var messages = Json(upstream.Requests[1].Body)["messages"]!.AsArray();
        var calls = messages[3]!["tool_calls"]!.AsArray();
        Assert.Equal(Retrieve, calls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("{\"p\":1}", calls[1]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal(["call_r", "call_c"], messages.Skip(4).Select(static tool => tool!["tool_call_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_lossy_round_ends_with_an_error_chunk_and_one_done()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Round("", Calls((0, "", CcrRetrieval.Name, Retrieve)), "tool_calls", 4));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Single(upstream.Requests);
        Assert.Contains("could not rebuild", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "[DONE]"));
        Assert.EndsWith("data: [DONE]\n\n", client, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_choice_with_an_index_other_than_zero_makes_the_round_lossy()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        const string Second = """data: {"id":"gen-1","object":"chat.completion.chunk","choices":[{"index":1,"delta":{"content":"x"},"finish_reason":null}]}""" + "\n\n";
        upstream.Handler = Script(Finished(ChatSse.Chunk("gen-1", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), null, null), Second, ChatSse.Chunk("gen-1", new JsonObject(), "tool_calls", null)), Round("answer", null, "stop", 6));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Single(upstream.Requests);
        Assert.Contains("could not rebuild", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "[DONE]"));
    }

    [Fact]
    public async Task A_proxy_retrieve_call_at_a_stop_finish_is_answered()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Round("", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), "stop", 4), Round("answer", null, "stop", 6));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Equal(2, upstream.Requests.Count);
        Assert.Contains("answer", client, StringComparison.Ordinal);
        Assert.DoesNotContain(CcrRetrieval.Name, client, StringComparison.Ordinal);
    }

    [Fact]
    public async Task At_a_length_finish_the_client_call_is_released_proxy_retrieve_is_dropped_and_the_drop_is_logged()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Round("", Calls((0, "call_r", CcrRetrieval.Name, Retrieve), (1, "call_c", "read", "{}")), "length", 4));
        var logs = new RecordingLoggerProvider();
        await using var proxy = await StartProxyAsync(logs: logs);

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Single(upstream.Requests);
        Assert.Contains("call_c", client, StringComparison.Ordinal);
        Assert.DoesNotContain(CcrRetrieval.Name, client, StringComparison.Ordinal);
        Assert.DoesNotContain("call_r", client, StringComparison.Ordinal);
        Assert.Contains("\"finish_reason\":\"length\"", client, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, static entry => entry.EventId == 1614);
    }

    [Fact]
    public async Task A_call_without_an_id_or_name_never_reaches_the_client()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = Script(Round("", Calls((0, "", "read", "{}")), "tool_calls", 4));
        await using var proxy = await StartProxyAsync();

        var client = await SendAsync(proxy, upstream, Request());

        Assert.DoesNotContain("\"name\":\"read\"", client, StringComparison.Ordinal);
        Assert.Contains("could not rebuild", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "[DONE]"));
    }

    [Fact]
    public async Task A_slow_follow_up_gets_comment_keep_alives_and_never_a_ping_event()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var calls = 0;
        upstream.Handler = async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            if (Interlocked.Increment(ref calls) == 1)
            {
                await context.Response.WriteAsync(Round("", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), "tool_calls", 4));
                return;
            }

            await Task.Delay(700, TestContext.Current.CancellationToken);
            await context.Response.WriteAsync(Round("answer", null, "stop", 6));
        };
        await using var proxy = await StartProxyAsync(keepAlive: "00:00:00.100");

        var client = await SendAsync(proxy, upstream, Request());

        Assert.Contains(": keep-alive\n\n", client, StringComparison.Ordinal);
        Assert.DoesNotContain("event: ping", client, StringComparison.Ordinal);
        Assert.Contains("answer", client, StringComparison.Ordinal);
        Assert.Equal(1, Count(client, "[DONE]"));
    }

    [Fact]
    public async Task A_follow_up_that_never_sends_headers_ends_the_turn_with_an_error_chunk_then_done()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await FakeUpstream.StartAsync();
        var calls = 0;
        upstream.Handler = async (_, context) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(Round("", Calls((0, "call_r", CcrRetrieval.Name, Retrieve)), "tool_calls", 4));
                return;
            }

            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        };
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using var proxy = await StartProxyAsync(keepAlive: "00:00:05", time: time);

        var sending = SendAsync(proxy, upstream, Request());
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        time.Advance(AiProxy.Ccr.CcrRelay.HeadersDeadline + TimeSpan.FromSeconds(1));
        var client = await sending.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(1, ChunksOf(client).Count(static chunk => chunk["error"] is not null));
        Assert.Equal(1, Count(client, "[DONE]"));
        Assert.EndsWith("data: [DONE]\n\n", client, StringComparison.Ordinal);
        Assert.DoesNotContain(": keep-alive", client[client.IndexOf("\"error\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }
}
