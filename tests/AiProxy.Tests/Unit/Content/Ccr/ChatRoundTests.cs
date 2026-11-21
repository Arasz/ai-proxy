using System.Text;
using System.Text.Json.Nodes;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Ccr.Loop;
using static AiProxy.Tests.Unit.Content.Ccr.RetrievalLoopTests;

namespace AiProxy.Tests.Unit.Content.Ccr;

public sealed class ChatRoundTests
{
    private static string Call(int index, string? id, string? name, string arguments)
    {
        var function = new JsonObject { ["arguments"] = arguments };
        if (name is not null)
        {
            function["name"] = name;
        }

        var call = new JsonObject { ["index"] = index, ["function"] = function };
        if (id is not null)
        {
            call["id"] = id;
            call["type"] = "function";
        }

        return ChatSse.Chunk("c1", new JsonObject { ["tool_calls"] = new JsonArray(call) }, null, null);
    }

    private static string Stream(params string[] chunks)
    {
        return string.Concat(chunks) + "data: [DONE]\n\n";
    }

    private static string Finish(string reason)
    {
        return ChatSse.Chunk("c1", new JsonObject(), reason, null);
    }

    private static string Usage(int completionTokens)
    {
        return ChatSse.Chunk("c1", null, null, ChatSse.Usage(completionTokens));
    }

    private static async Task<(IReadOnlyList<SseEvent> Passed, ChatRound Round)> ReadAsync(string wire, bool clientUsage = false, ChatRound? round = null)
    {
        round ??= ChatRound.First(clientUsage);
        var passed = new List<SseEvent>();
        await foreach (var sse in new SseReader().ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(wire)), CancellationToken.None))
        {
            passed.AddRange(round.Accept(sse));
        }

        return (passed, round);
    }

    [Fact]
    public async Task Tool_call_deltas_are_held_and_a_call_split_over_four_deltas_is_joined_under_its_first_id_and_name()
    {
        var wire = Stream(
            ChatSse.Chunk("c1", ChatSse.Text("hi"), null, null),
            Call(0, "call_1", "proxy_retrieve", ""),
            Call(0, null, null, "{\"id\":\"abc"),
            Call(0, null, null, "def\",\"mode\""),
            Call(0, null, null, ":\"whole\"}"),
            Finish("tool_calls"));

        var (passed, round) = await ReadAsync(wire);

        Assert.DoesNotContain(passed, static sse => sse.Data.Contains("tool_calls", StringComparison.Ordinal));
        Assert.Contains(passed, static sse => sse.Data.Contains("\"hi\"", StringComparison.Ordinal));
        var call = Assert.Single(round.ToolCalls);
        Assert.Equal(new ToolCall("call_1", "proxy_retrieve", """{"id":"abcdef","mode":"whole"}"""), call);
        Assert.True(round.StopsForTools);
        Assert.True(round.Ended);
    }

    [Fact]
    public async Task Calls_are_joined_by_index_when_their_deltas_interleave()
    {
        var wire = Stream(Call(0, "a", "read", "{\"p\":"), Call(1, "b", "bash", "{\"c\":"), Call(0, null, null, "1}"), Call(1, null, null, "2}"), Finish("tool_calls"));

        var (_, round) = await ReadAsync(wire);

        Assert.Equal([new ToolCall("a", "read", "{\"p\":1}"), new ToolCall("b", "bash", "{\"c\":2}")], round.ToolCalls);
    }

    [Fact]
    public async Task Released_client_calls_are_numbered_from_zero_and_proxy_retrieve_never_reaches_the_client()
    {
        var wire = Stream(Call(2, "x", "read", "{}"), Call(3, "y", "proxy_retrieve", "{}"), Finish("length"));
        var (_, round) = await ReadAsync(wire);

        var released = round.Finish(0, 0);

        var calls = released.Select(static sse => sse.Data).Where(static data => data.Contains("tool_calls", StringComparison.Ordinal)).ToArray();
        var only = Assert.Single(calls);
        Assert.Equal(0, JsonNode.Parse(only)!["choices"]![0]!["delta"]!["tool_calls"]![0]!["index"]!.GetValue<int>());
        Assert.DoesNotContain("proxy_retrieve", string.Concat(released.Select(static sse => sse.Data)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_final_message_ends_in_one_done_and_the_usage_chunk_exists_only_when_the_client_asked_for_it()
    {
        var wire = Stream(ChatSse.Chunk("c1", ChatSse.Text("hi"), null, null), Finish("stop"), Usage(7));

        var (_, without) = await ReadAsync(wire, clientUsage: false);
        var (_, with) = await ReadAsync(wire, clientUsage: true);

        var plain = without.Finish(0, 7);
        var asked = with.Finish(0, 7);
        Assert.Equal("[DONE]", plain[^1].Data);
        Assert.DoesNotContain(plain, static sse => sse.Data.Contains("usage", StringComparison.Ordinal));
        Assert.Equal("[DONE]", asked[^1].Data);
        Assert.Equal(1, asked.Count(static sse => sse.Data == "[DONE]"));
        var usage = JsonNode.Parse(asked[^2].Data)!;
        Assert.Empty(usage["choices"]!.AsArray());
        Assert.Equal(7, usage["usage"]!["completion_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_single_round_usage_goes_to_the_client_as_the_upstream_reported_it()
    {
        var (_, round) = await ReadAsync(Stream(ChatSse.Chunk("c1", ChatSse.Text("x"), null, null), Finish("stop"), Usage(6)), clientUsage: true);

        var usage = JsonNode.Parse(round.Finish(0, 0)[^2].Data)!["usage"]!;

        Assert.Equal(6, usage["completion_tokens"]!.GetValue<int>());
        Assert.Equal(3, usage["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(9, usage["total_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Usage_sums_every_numeric_field_over_the_rounds_including_cost_and_nested_details()
    {
        string Wire(JsonObject usage)
        {
            return Stream(ChatSse.Chunk("c1", ChatSse.Text("x"), null, null), Finish("stop"), ChatSse.Chunk("c1", null, null, usage));
        }

        var first = await ReadAsync(
            Wire(JsonNode.Parse("""{"prompt_tokens":10,"completion_tokens":4,"total_tokens":14,"cost":0.5,"prompt_tokens_details":{"cached_tokens":2}}""")!.AsObject()),
            clientUsage: true);
        var (_, second) = await ReadAsync(
            Wire(JsonNode.Parse("""{"prompt_tokens":20,"completion_tokens":6,"total_tokens":26,"cost":0.25,"prompt_tokens_details":{"cached_tokens":5}}""")!.AsObject()),
            round: (ChatRound)first.Round.Next());

        var usage = JsonNode.Parse(second.Finish(1, 999)[^2].Data)!["usage"]!;

        Assert.Equal(30, usage["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(10, usage["completion_tokens"]!.GetValue<int>());
        Assert.Equal(40, usage["total_tokens"]!.GetValue<int>());
        Assert.Equal(0.75, usage["cost"]!.GetValue<double>(), 6);
        Assert.Equal(7, usage["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_follow_up_round_drops_the_role_chunk_and_keeps_the_client_chunk_id()
    {
        var first = await ReadAsync(Stream(ChatSse.Chunk("c1", new JsonObject { ["role"] = "assistant" }, null, null), Finish("tool_calls")));
        var next = (ChatRound)first.Round.Next();

        var (passed, _) = await ReadAsync(
            ChatSse.Chunk("c2", new JsonObject { ["role"] = "assistant" }, null, null) + ChatSse.Chunk("c2", ChatSse.Text("more"), null, null) + "data: [DONE]\n\n", round: next);

        var only = Assert.Single(passed);
        Assert.Equal("c1", JsonNode.Parse(only.Data)!["id"]!.GetValue<string>());
        Assert.Equal("more", JsonNode.Parse(only.Data)!["choices"]![0]!["delta"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_stateless_follow_up_echoes_text_reasoning_and_every_call_and_answers_each_call_id()
    {
        var reasoning = new JsonObject { ["reasoning"] = "think" };
        var details = new JsonObject { ["reasoning_details"] = new JsonArray(new JsonObject { ["type"] = "reasoning.text", ["text"] = "t" }) };
        var wire = Stream(
            ChatSse.Chunk("c1", reasoning, null, null),
            ChatSse.Chunk("c1", details, null, null),
            ChatSse.Chunk("c1", ChatSse.Text("reading"), null, null),
            Call(0, "call_r", "proxy_retrieve", "{}"),
            Call(1, "call_c", "read", "{}"),
            Finish("tool_calls"));
        var (_, round) = await ReadAsync(wire);

        var body = round.FollowUp(
            Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"q"}]}"""),
            [ToolResult.Of("call_r", new CcrRetrieval("TEXT", null)), ToolResult.Deferred("call_c")]);

        var messages = JsonNode.Parse(body)!["messages"]!.AsArray();
        Assert.Equal(4, messages.Count);
        var assistant = messages[1]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        Assert.Equal("reading", assistant["content"]!.GetValue<string>());
        Assert.Equal("think", assistant["reasoning"]!.GetValue<string>());
        Assert.Equal("reasoning.text", assistant["reasoning_details"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(["call_r", "call_c"], assistant["tool_calls"]!.AsArray().Select(static call => call!["id"]!.GetValue<string>()));
        Assert.Equal("tool", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("call_r", messages[2]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("TEXT", messages[2]!["content"]!.GetValue<string>());
        Assert.Equal("call_c", messages[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal(RetrievalLoop.DeferredCall, messages[3]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_error_chunk_marks_the_round_failed_and_passes_on()
    {
        var (passed, round) = await ReadAsync("data: {\"error\":{\"message\":\"boom\",\"code\":500}}\n\ndata: [DONE]\n\n");

        Assert.True(round.Failed);
        Assert.Contains("boom", Assert.Single(passed).Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comment_lines_are_forwarded_as_keep_alives_and_a_stream_without_done_has_not_ended()
    {
        var (passed, round) = await ReadAsync(": OPENROUTER PROCESSING\n\n" + ChatSse.Chunk("c1", ChatSse.Text("hi"), null, null) + ": ping\n\n");

        Assert.Equal([SseEvent.CommentLine, SseEvent.CommentLine], passed.Where(static sse => sse.Name == SseEvent.Comment).ToArray());
        Assert.Single(passed, static sse => sse.Name != SseEvent.Comment);
        Assert.False(round.Ended);
        Assert.False(round.Failed);
    }

    [Fact]
    public async Task A_call_that_never_got_an_id_or_name_makes_the_round_lossy()
    {
        var (_, round) = await ReadAsync(Stream(Call(0, null, null, "{}"), Finish("tool_calls")));

        Assert.True(round.Lossy);
    }

    [Fact]
    public void Events_are_written_without_an_event_line_and_keep_alives_as_comments()
    {
        var chat = Encoding.UTF8.GetString(ChatRound.First(false).Error("x").ToBytes());

        Assert.StartsWith("data: ", chat, StringComparison.Ordinal);
        Assert.Equal(": keep-alive\n\n", Encoding.UTF8.GetString(ChatRound.First(false).KeepAlive.ToBytes()));
        Assert.Equal("data: [DONE]\n\n", Encoding.UTF8.GetString(ChatRound.First(false).Terminator!.ToBytes()));
    }

    private static string Extras(JsonObject extras, string? text = null)
    {
        if (text is not null)
        {
            extras["content"] = text;
        }

        return ChatSse.Chunk("c1", extras, null, null);
    }

    private static string CallWithoutIndex(string arguments)
    {
        return ChatSse.Chunk("c1", new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["function"] = new JsonObject { ["arguments"] = arguments } }) }, null, null);
    }

    private static JsonObject FollowUpOf(ChatRound round, params ToolResult[] results)
    {
        var body = round.FollowUp(Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"q"}]}"""), results);
        return JsonNode.Parse(body)!["messages"]!.AsArray()[1]!.AsObject();
    }

    [Fact]
    public async Task A_json_null_after_reasoning_ended_does_not_erase_the_collected_reasoning_or_refusal()
    {
        var wire = Stream(
            Extras(new JsonObject { ["reasoning"] = "think", ["refusal"] = "no" }),
            Extras(new JsonObject { ["reasoning"] = null, ["refusal"] = null }, "go"),
            Call(0, "call_r", "proxy_retrieve", "{}"),
            Finish("tool_calls"));
        var (_, round) = await ReadAsync(wire);

        var assistant = FollowUpOf(round, ToolResult.Deferred("call_r"));

        Assert.Equal("think", assistant["reasoning"]!.GetValue<string>());
        Assert.Equal("no", assistant["refusal"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("tool_calls", true)]
    [InlineData("stop", true)]
    [InlineData("length", false)]
    [InlineData("content_filter", false)]
    public async Task Calls_held_at_a_tool_calls_or_stop_finish_stop_the_round_for_tools_and_at_any_other_finish_do_not(string finish, bool stops)
    {
        var (_, round) = await ReadAsync(Stream(Call(0, "call_r", "proxy_retrieve", "{}"), Finish(finish)));

        Assert.Equal(stops, round.StopsForTools);
    }

    [Fact]
    public async Task A_proxy_retrieve_call_at_stop_is_answered_by_the_loop()
    {
        var (_, round) = await ReadAsync(Stream(Call(0, "call_r", "proxy_retrieve", "{}"), Finish("stop")));

        Assert.Equal(RetrievalStep.Retrieve, new RetrievalLoop(round).Decide());
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task At_a_truncated_finish_the_client_call_is_released_and_proxy_retrieve_is_dropped(string finish)
    {
        var (_, round) = await ReadAsync(Stream(Call(0, "call_r", "proxy_retrieve", "{}"), Call(1, "call_c", "read", "{}"), Finish(finish)));
        var loop = new RetrievalLoop(round);

        Assert.Equal(RetrievalStep.Release, loop.Decide());
        var released = string.Concat(loop.Finish().Select(static sse => sse.Data));
        Assert.Contains("call_c", released, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy_retrieve", released, StringComparison.Ordinal);
        Assert.Contains($"\"finish_reason\":\"{finish}\"", released, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_details_pieces_with_the_same_index_are_joined_and_keep_the_last_signature()
    {
        JsonObject Piece(int index, string text, string? signature = null)
        {
            var piece = new JsonObject { ["type"] = "reasoning.text", ["index"] = index, ["text"] = text, ["format"] = "unknown" };
            if (signature is not null)
            {
                piece["signature"] = signature;
            }

            return piece;
        }

        var wire = Stream(
            Extras(new JsonObject { ["reasoning_details"] = new JsonArray(Piece(0, "ab")) }),
            Extras(new JsonObject { ["reasoning_details"] = new JsonArray(Piece(0, "cd"), Piece(1, "xy")) }),
            Extras(new JsonObject { ["reasoning_details"] = new JsonArray(Piece(0, "", "sig")) }),
            Call(0, "call_r", "proxy_retrieve", "{}"),
            Finish("tool_calls"));
        var (_, round) = await ReadAsync(wire);

        var details = FollowUpOf(round, ToolResult.Deferred("call_r"))["reasoning_details"]!.AsArray();

        Assert.Equal(2, details.Count);
        Assert.Equal("abcd", details[0]!["text"]!.GetValue<string>());
        Assert.Equal("sig", details[0]!["signature"]!.GetValue<string>());
        Assert.Equal(0, details[0]!["index"]!.GetValue<int>());
        Assert.Equal("xy", details[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_repeated_index_with_a_different_id_opens_a_new_call_and_both_get_a_tool_reply()
    {
        var (_, round) = await ReadAsync(Stream(Call(0, "a", "proxy_retrieve", ""), Call(0, null, null, "{\"x\":1}"), Call(0, "b", "read", "{}"), Finish("tool_calls")));
        var loop = new RetrievalLoop(round);
        loop.Decide();

        var results = loop.ToolResults(new Dictionary<string, CcrRetrieval> { ["a"] = new("T", null) });
        var assistant = FollowUpOf(round, [.. results]);

        Assert.Equal([new ToolCall("a", "proxy_retrieve", "{\"x\":1}"), new ToolCall("b", "read", "{}")], round.ToolCalls);
        Assert.Equal(["a", "b"], assistant["tool_calls"]!.AsArray().Select(static call => call!["id"]!.GetValue<string>()));
        Assert.Equal(["a", "b"], results.Select(static result => result.ToolUseId));
        Assert.False(round.Lossy);
    }

    [Fact]
    public async Task A_delta_without_an_index_continues_the_last_call()
    {
        var (_, round) = await ReadAsync(Stream(Call(0, "a", "read", "{\"p\":"), CallWithoutIndex("1}"), Finish("tool_calls")));

        Assert.Equal([new ToolCall("a", "read", "{\"p\":1}")], round.ToolCalls);
    }

    [Fact]
    public void The_anthropic_round_drops_a_comment_event_without_failing()
    {
        var round = RetrievalRound.First(CcrMode.Stateless);

        Assert.Empty(round.Accept(new SseEvent(SseEvent.Comment, "OPENROUTER PROCESSING")));
        Assert.False(round.Failed);
    }

    [Fact]
    public async Task An_error_chunk_of_a_follow_up_round_carries_the_chunk_id_the_client_already_saw()
    {
        var first = await ReadAsync(Stream(ChatSse.Chunk("c1", ChatSse.Text("hi"), null, null), Finish("tool_calls")));

        var (passed, _) = await ReadAsync("data: {\"id\":\"c2\",\"error\":{\"message\":\"boom\",\"code\":500}}\n\ndata: [DONE]\n\n", round: (ChatRound)first.Round.Next());

        Assert.Equal("c1", JsonNode.Parse(Assert.Single(passed).Data)!["id"]!.GetValue<string>());
    }
}
