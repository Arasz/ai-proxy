using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Ccr.Loop;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>The retrieval loop over recorded Messages streams: what the client sees, and what goes back upstream.</summary>
public sealed class RetrievalLoopTests
{
    private const string Retrieve = """{"id":"0123456789abcdef","mode":"whole"}""";

    [Fact]
    public async Task A_message_without_proxy_retrieve_reaches_the_client_byte_for_byte()
    {
        var stream = Sse.Message("msg_1", "tool_use", 7, Sse.Text("looking"), Sse.Tool("tu_1", "Read", """{"file_path":"/a.cs"}"""));
        var loop = new RetrievalLoop();

        var written = await RunRound(loop, stream);
        Assert.Equal(RetrievalStep.Release, loop.Decide());
        written.AddRange(loop.Finish());

        Assert.Equal(stream, Wire(written));
        Assert.Equal(RetrievalState.Done, loop.State);
    }

    [Fact]
    public async Task Tool_calls_are_held_until_the_message_ends()
    {
        var stream = Sse.Message("msg_1", "tool_use", 7, Sse.Tool("tu_1", "Read", "{}"), Sse.Text("after"));
        var loop = new RetrievalLoop();

        var duringStream = await RunRound(loop, stream);

        Assert.DoesNotContain(duringStream, sse => sse.Data.Contains("tool_use", StringComparison.Ordinal) && sse.Name == "content_block_start");
        Assert.Contains(duringStream, sse => sse.Name == "content_block_start" && sse.Data.Contains("\"index\":0", StringComparison.Ordinal) && sse.Data.Contains("\"text\"", StringComparison.Ordinal));
        loop.Decide();
        var released = loop.Finish();
        Assert.Contains(released, sse => sse.Name == "content_block_start" && sse.Data.Contains("\"index\":1", StringComparison.Ordinal) && sse.Data.Contains("tu_1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_retrieving_round_merges_into_one_message_without_the_retrieval_or_the_deferred_call()
    {
        var loop = new RetrievalLoop();
        var written = await RunRound(loop, Sse.Message("msg_1", "tool_use", 10,
            Sse.Thinking("hmm", "sig-1"),
            Sse.Text("let me read"),
            Sse.Tool("tu_r", CcrRetrieval.Name, Retrieve),
            Sse.Tool("tu_c", "Read", """{"file_path":"/b.cs"}""")));

        Assert.Equal(RetrievalStep.Retrieve, loop.Decide());
        var results = loop.ToolResults(new Dictionary<string, CcrRetrieval> { ["tu_r"] = new("the whole text", null) });
        Assert.Equal(2, results.Count);
        Assert.Equal([new ToolResult("tu_r", "the whole text", ToolOutcome.Succeeded), ToolResult.Deferred("tu_c")], results);

        loop.NextRound();
        written.AddRange(await RunRound(loop, Sse.Message("msg_2", "end_turn", 5, Sse.Text("done"))));
        Assert.Equal(RetrievalStep.Release, loop.Decide());
        written.AddRange(loop.Finish());

        Assert.Single(written, sse => sse.Name == "message_start");
        Assert.Contains("msg_1", written.Single(sse => sse.Name == "message_start").Data, StringComparison.Ordinal);
        Assert.DoesNotContain(written, sse => sse.Data.Contains("tu_r", StringComparison.Ordinal) || sse.Data.Contains("tu_c", StringComparison.Ordinal));
        var starts = written.Where(sse => sse.Name == "content_block_start").Select(sse => Data(sse).GetProperty("index").GetInt32());
        Assert.Equal([0, 1, 2], starts);
        var delta = Data(written.Single(sse => sse.Name == "message_delta"));
        Assert.Equal("end_turn", delta.GetProperty("delta").GetProperty("stop_reason").GetString());
        Assert.Equal(15, delta.GetProperty("usage").GetProperty("output_tokens").GetInt64());
        Assert.Equal("msg_1", loop.FirstMessageId);
        Assert.Equal("msg_2", loop.Round.MessageId);
    }

    [Fact]
    public async Task The_assistant_content_is_rebuilt_as_received_for_a_stateless_follow_up()
    {
        var loop = new RetrievalLoop();
        await RunRound(loop, Sse.Message("msg_1", "tool_use", 10,
            Sse.Thinking("think ", "sig-1"),
            Sse.Text("hi"),
            Sse.Tool("tu_r", CcrRetrieval.Name, Retrieve),
            Sse.Tool("tu_e", "Bash", "")));

        var content = ((RetrievalRound)loop.Round).Content();

        Assert.Equal(
            """[{"type":"thinking","thinking":"think ","signature":"sig-1"},{"type":"text","text":"hi"},{"type":"tool_use","id":"tu_r","name":"proxy_retrieve","input":{"id":"0123456789abcdef","mode":"whole"}},{"type":"tool_use","id":"tu_e","name":"Bash","input":{}}]""",
            content.ToJsonString());
        Assert.False(loop.Round.Lossy);
    }

    [Fact]
    public async Task Three_rounds_are_answered_the_fourth_gets_the_limit_and_a_fifth_call_fails()
    {
        var loop = new RetrievalLoop();
        for (var round = 1; round <= 4; round++)
        {
            await RunRound(loop, Sse.Message($"msg_{round}", "tool_use", 1, Sse.Tool($"tu_{round}", CcrRetrieval.Name, Retrieve)));
            Assert.Equal(RetrievalStep.Retrieve, loop.Decide());
            Assert.Equal(round == 4, loop.AtLimit);
            loop.NextRound();
        }

        await RunRound(loop, Sse.Message("msg_5", "tool_use", 1, Sse.Tool("tu_5", CcrRetrieval.Name, Retrieve)));

        Assert.Equal(RetrievalStep.Fail, loop.Decide());
        Assert.Equal(RetrievalState.Failed, loop.State);
    }

    [Fact]
    public async Task A_retrieval_cut_off_by_max_tokens_is_dropped_and_the_other_calls_are_released()
    {
        var loop = new RetrievalLoop();
        await RunRound(loop, Sse.Message("msg_1", "max_tokens", 1, Sse.Tool("tu_c", "Read", "{}"), Sse.Tool("tu_r", CcrRetrieval.Name, """{"id":"01""")));

        Assert.Equal(RetrievalStep.Release, loop.Decide());
        var released = loop.Finish();

        Assert.Contains(released, sse => sse.Data.Contains("tu_c", StringComparison.Ordinal));
        Assert.DoesNotContain(released, sse => sse.Data.Contains("tu_r", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_upstream_error_event_is_passed_on_and_fails_the_loop()
    {
        var loop = new RetrievalLoop();
        var stream = Sse.Wire(new SseEvent("message_start", """{"type":"message_start","message":{"id":"msg_1"}}"""), SseEvent.Error("overloaded_error", "Overloaded"));

        var written = await RunRound(loop, stream);

        Assert.Contains(written, sse => sse.Name == "error");
        Assert.Equal(RetrievalStep.Fail, loop.Decide());
    }

    [Fact]
    public void A_move_the_machine_does_not_declare_throws()
    {
        var loop = new RetrievalLoop();

        Assert.Throws<InvalidOperationException>(() => loop.NextRound());
    }

    [Fact]
    public void A_thread_follow_up_keeps_every_field_and_continues_from_the_retrieving_response()
    {
        const string forwarded =
            """{"model":"claude-opus-5-5","max_tokens":10,"stream":true,"system":[{"type":"text","text":"s"}],"tools":[{"name":"Read"}],"messages":[{"role":"user","content":"old"}],"thread":{"type":"create"},"diagnostics":{"previous_message_id":"msg_0"},"metadata":{"user_id":"u"}}""";
        ToolResult[] results = [new("tu_r", "zażółć <x>", ToolOutcome.Succeeded), ToolResult.Deferred("tu_c")];

        var body = Encoding.UTF8.GetString(RetrievalFollowUp.Thread(Encoding.UTF8.GetBytes(forwarded), "msg_1", results));

        Assert.Equal(
            $$$"""{"model":"claude-opus-5-5","max_tokens":10,"stream":true,"system":[{"type":"text","text":"s"}],"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_r","content":"zażółć <x>"},{"type":"tool_result","tool_use_id":"tu_c","content":"{{{RetrievalLoop.DeferredCall}}}","is_error":true}]}],"thread":{"type":"continue","previous_message_id":"msg_1"},"diagnostics":{"previous_message_id":"msg_1"},"metadata":{"user_id":"u"}}""",
            body);
    }

    [Fact]
    public void A_stateless_follow_up_appends_the_assistant_message_and_the_tool_results()
    {
        const string forwarded = """{"model":"m","messages":[{"role":"user","content":"q"}],"tools":[{"name":"Read"}]}""";

        var body = Encoding.UTF8.GetString(RetrievalFollowUp.Stateless(Encoding.UTF8.GetBytes(forwarded), [new JsonObject { ["type"] = "text", ["text"] = "a" }], [new ToolResult("tu_r", "b", ToolOutcome.Succeeded)]));

        Assert.Equal(
            """{"model":"m","messages":[{"role":"user","content":"q"},{"role":"assistant","content":[{"type":"text","text":"a"}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_r","content":"b"}]}],"tools":[{"name":"Read"}]}""",
            body);
    }

    private static async Task<List<SseEvent>> RunRound(RetrievalLoop loop, string wire)
    {
        var written = new List<SseEvent>();
        await foreach (var sse in new SseReader().ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(wire)), CancellationToken.None))
        {
            written.AddRange(loop.Round.Accept(sse));
        }

        return written;
    }

    private static JsonElement Data(SseEvent sse)
    {
        return JsonDocument.Parse(sse.Data).RootElement.Clone();
    }

    private static string Wire(IEnumerable<SseEvent> events)
    {
        return string.Concat(events.Select(sse => Encoding.UTF8.GetString(sse.ToBytes())));
    }

    [Fact]
    public async Task Chat_chunks_built_by_the_test_builder_parse_back_as_a_tool_call_round()
    {
        var wire = ChatSse.Message("chatcmpl-1", "tool_calls", ChatSse.ToolCall(0, "call_1", "proxy_retrieve", Retrieve), ChatSse.Usage(7));

        var events = await ParseAsync(wire);

        Assert.All(events.Take(events.Count - 1), sse => Assert.Equal("chat.completion.chunk", JsonNode.Parse(sse.Data)!["object"]!.GetValue<string>()));
        Assert.Equal("[DONE]", events[^1].Data);
        var call = JsonNode.Parse(events[1].Data)!["choices"]![0]!["delta"]!["tool_calls"]![0]!;
        Assert.Equal("call_1", call["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Responses_events_built_by_the_test_builder_parse_back_in_order()
    {
        var wire = ResponsesSse.Message("resp_1", ResponsesSse.FunctionCall("fc_1", "call_1", "proxy_retrieve", Retrieve), 9);

        var events = await ParseAsync(wire);

        Assert.Equal(["response.created", "response.output_item.added", "response.function_call_arguments.done", "response.output_item.done", "response.completed"], events.Select(sse => sse.Name));
        Assert.Equal(9, JsonNode.Parse(events[^1].Data)!["response"]!["usage"]!["output_tokens"]!.GetValue<int>());
    }

    private static async Task<List<SseEvent>> ParseAsync(string wire)
    {
        var events = new List<SseEvent>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        await foreach (var sse in new SseReader().ReadAsync(stream, CancellationToken.None))
        {
            events.Add(sse);
        }

        return events;
    }

    /// <summary>Builds Messages streams the way the API sends them.</summary>
    internal static class Sse
    {
        public static string Wire(params SseEvent[] events)
        {
            return string.Concat(events.Select(sse => Encoding.UTF8.GetString(sse.ToBytes())));
        }

        public static Func<int, SseEvent[]> Text(string text)
        {
            return index =>
            [
                Event("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } }),
                Delta(index, new JsonObject { ["type"] = "text_delta", ["text"] = text }),
                Stop(index)
            ];
        }

        public static Func<int, SseEvent[]> Thinking(string thinking, string signature)
        {
            return index =>
            [
                Event("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" } }),
                Delta(index, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinking }),
                Delta(index, new JsonObject { ["type"] = "signature_delta", ["signature"] = signature }),
                Stop(index)
            ];
        }

        public static Func<int, SseEvent[]> Tool(string id, string name, string input)
        {
            return index =>
            [
                Event("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() } }),
                .. input.Length == 0 ? Array.Empty<SseEvent>() : [Delta(index, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = input })],
                Stop(index)
            ];
        }

        public static string Message(string id, string stopReason, int outputTokens, params Func<int, SseEvent[]>[] blocks)
        {
            return Wire(
            [
                Event("message_start",
                    new JsonObject
                    {
                        ["type"] = "message_start",
                        ["message"] = new JsonObject { ["id"] = id, ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(), ["usage"] = new JsonObject { ["input_tokens"] = 3, ["output_tokens"] = 1 } }
                    }),
                .. blocks.SelectMany((block, index) => block(index)),
                Event("message_delta",
                    new JsonObject
                    {
                        ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null }, ["usage"] = new JsonObject { ["input_tokens"] = 3, ["output_tokens"] = outputTokens }
                    }),
                new SseEvent("message_stop", """{"type":"message_stop"}""")
            ]);
        }

        private static SseEvent Event(string name, JsonObject data)
        {
            return new SseEvent(name, data.ToJsonString());
        }

        private static SseEvent Delta(int index, JsonObject delta)
        {
            return Event("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta });
        }

        private static SseEvent Stop(int index)
        {
            return Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
        }
    }

    /// <summary>Builds Chat Completions streams the way the API sends them: unnamed <c>chat.completion.chunk</c> data lines and <c>[DONE]</c>.</summary>
    internal static class ChatSse
    {
        public static JsonObject ToolCall(int index, string id, string name, string arguments)
        {
            return new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = index, ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } })
            };
        }

        public static JsonObject Text(string text)
        {
            return new JsonObject { ["content"] = text };
        }

        public static JsonObject Usage(int completionTokens)
        {
            return new JsonObject { ["completion_tokens"] = completionTokens, ["prompt_tokens"] = 3, ["total_tokens"] = 3 + completionTokens };
        }

        public static string Message(string id, string finishReason, JsonObject delta, JsonObject usage)
        {
            return string.Concat(
                Chunk(id, new JsonObject { ["role"] = "assistant" }, null, null),
                Chunk(id, delta, null, null),
                Chunk(id, new JsonObject(), finishReason, null),
                Chunk(id, null, null, usage),
                "data: [DONE]\n\n");
        }

        public static string Chunk(string id, JsonObject? delta, string? finishReason, JsonObject? usage)
        {
            var choices = new JsonArray();
            if (delta is not null)
            {
                choices.Add(new JsonObject { ["index"] = 0, ["delta"] = delta.DeepClone(), ["finish_reason"] = finishReason });
            }

            var chunk = new JsonObject { ["id"] = id, ["object"] = "chat.completion.chunk", ["choices"] = choices };
            if (usage is not null)
            {
                chunk["usage"] = usage.DeepClone();
            }

            return $"data: {chunk.ToJsonString()}\n\n";
        }
    }

    /// <summary>Builds Responses API streams the way the API sends them: named <c>response.*</c> events.</summary>
    internal static class ResponsesSse
    {
        public static JsonObject FunctionCall(string itemId, string callId, string name, string arguments)
        {
            return new JsonObject { ["type"] = "function_call", ["id"] = itemId, ["call_id"] = callId, ["name"] = name, ["arguments"] = arguments };
        }

        public static string Message(string id, JsonObject item, int outputTokens)
        {
            return string.Concat(
                Event("response.created", new JsonObject { ["response"] = new JsonObject { ["id"] = id, ["status"] = "in_progress" } }),
                Event("response.output_item.added", new JsonObject { ["output_index"] = 0, ["item"] = item.DeepClone() }),
                Event("response.function_call_arguments.done", new JsonObject { ["output_index"] = 0, ["arguments"] = item["arguments"]!.GetValue<string>() }),
                Event("response.output_item.done", new JsonObject { ["output_index"] = 0, ["item"] = item.DeepClone() }),
                Event("response.completed", new JsonObject { ["response"] = new JsonObject { ["id"] = id, ["status"] = "completed", ["usage"] = new JsonObject { ["output_tokens"] = outputTokens } } }));
        }

        private static string Event(string name, JsonObject data)
        {
            data["type"] = name;
            return $"event: {name}\ndata: {data.ToJsonString()}\n\n";
        }
    }
}
