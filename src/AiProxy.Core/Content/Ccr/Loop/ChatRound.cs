using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>
///     One upstream Chat Completions response inside a client response. Text and reasoning deltas pass on as
///     they arrive; <c>tool_calls</c> deltas, the finish chunk, the usage chunk and <c>[DONE]</c> are held until
///     the loop decides. The assistant message is rebuilt from what was received.
/// </summary>
/// <param name="dropRole">Whether the <c>role</c> of the first delta is dropped: a follow-up round continues the client's message.</param>
/// <param name="clientUsage">Whether the client asked for a usage chunk.</param>
/// <param name="clientId">The chunk id the client sees; empty until the first chunk of the first round names it.</param>
public sealed class ChatRound(bool dropRole, bool clientUsage, string clientId) : IRetrievalRound
{
    private const string Done = "[DONE]";
    private const string ToolCallsFinish = "tool_calls";
    private static readonly string[] Folded = ["role", "content", "tool_calls"];

    private readonly SortedDictionary<int, Call> _calls = [];
    private readonly StringBuilder _text = new();
    private readonly JsonObject _extras = [];
    private JsonObject _template = [];
    private JsonObject? _usage;
    private bool _split;

    /// <summary>The chunk id of the first upstream response: the one every chunk the client gets carries.</summary>
    public string ClientId => clientId.Length > 0 ? clientId : MessageId;

    /// <inheritdoc />
    public string MessageId { get; private set; } = "";

    /// <summary>The finish reason of the response; empty until it arrives.</summary>
    public string FinishReason { get; private set; } = "";

    /// <inheritdoc />
    public bool Ended { get; private set; }

    /// <inheritdoc />
    public bool Failed { get; private set; }

    /// <inheritdoc />
    public bool Lossy => _split || _calls.Values.Any(static call => call.Id.Length == 0 || call.Name.Length == 0);

    /// <inheritdoc />
    public bool StopsForTools => FinishReason == ToolCallsFinish && _calls.Count > 0;

    /// <inheritdoc />
    public long OutputTokens => _usage?["completion_tokens"] is JsonValue value && value.TryGetValue<long>(out var tokens) ? tokens : 0;

    /// <inheritdoc />
    public IReadOnlyList<ToolCall> ToolCalls => _calls.Values.Select(static call => new ToolCall(call.Id, call.Name, call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString())).ToArray();

    /// <inheritdoc />
    public SseEvent KeepAlive => SseEvent.CommentLine;

    /// <inheritdoc />
    public SseEvent? Terminator => Unnamed(Done);

    /// <summary>The round of the first upstream response.</summary>
    public static ChatRound First(bool clientUsage)
    {
        return new ChatRound(false, clientUsage, "");
    }

    /// <inheritdoc />
    public IReadOnlyList<SseEvent> Accept(SseEvent sse)
    {
        if (sse.Data == Done)
        {
            Ended = true;
            return [];
        }

        if (!TryParse(sse.Data, out var data))
        {
            return [Unnamed(sse.Data)];
        }

        if (data["error"] is not null)
        {
            Failed = true;
            return [Unnamed(data.ToJsonString(MessagesApi.Json))];
        }

        Remember(data);
        return data["choices"] is JsonArray choices ? Pass(data, choices) : [];
    }

    /// <inheritdoc />
    public IReadOnlyList<SseEvent> Finish(int answered, long outputTokens)
    {
        var events = new List<SseEvent>();
        var released = 0;
        foreach (var call in ToolCalls.Where(static call => !call.IsRetrieval))
        {
            var function = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Input };
            var delta = new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = released++, ["id"] = call.Id, ["type"] = "function", ["function"] = function }) };
            events.Add(Chunk(delta, null));
        }

        events.Add(Chunk([], FinishReason.Length > 0 ? FinishReason : "stop"));
        if (clientUsage)
        {
            var chunk = Base();
            chunk["choices"] = new JsonArray();
            chunk["usage"] = MergedUsage(outputTokens);
            events.Add(Unnamed(chunk.ToJsonString(MessagesApi.Json)));
        }

        events.Add(Terminator!);
        return events;
    }

    /// <inheritdoc />
    public IRetrievalRound Next()
    {
        return new ChatRound(true, clientUsage, ClientId);
    }

    /// <inheritdoc />
    public byte[] ThreadFollowUp(ReadOnlySpan<byte> forwarded, IReadOnlyList<ToolResult> toolResults)
    {
        throw new NotSupportedException("Chat Completions has no thread mode.");
    }

    /// <inheritdoc />
    public byte[] StatelessFollowUp(ReadOnlySpan<byte> forwarded, IReadOnlyList<ToolResult> toolResults)
    {
        if (JsonNode.Parse(forwarded) is not JsonObject request || request["messages"] is not JsonArray messages)
        {
            throw new InvalidOperationException("The forwarded request has no messages array to continue.");
        }

        messages.Add(Assistant());
        foreach (var result in toolResults)
        {
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.ToolUseId, ["content"] = result.Content });
        }

        return JsonSerializer.SerializeToUtf8Bytes(request, MessagesApi.Json);
    }

    /// <inheritdoc />
    public SseEvent Error(string message)
    {
        return Unnamed(new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = MessagesApi.Errors.Api, ["code"] = MessagesApi.Errors.Api } }.ToJsonString(MessagesApi.Json));
    }

    /// <inheritdoc />
    public SseEvent ErrorOf(string body)
    {
        return Unnamed(body);
    }

    private static SseEvent Unnamed(string data)
    {
        return new SseEvent(SseEvent.Unnamed, data);
    }

    private static bool TryParse(string text, out JsonObject data)
    {
        try
        {
            data = JsonNode.Parse(text) as JsonObject ?? [];
            return data.Count > 0;
        }
        catch (JsonException)
        {
            data = [];
            return false;
        }
    }

    private void Remember(JsonObject data)
    {
        if (MessageId.Length == 0)
        {
            MessageId = data["id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : "";
        }

        _template = (JsonObject)data.DeepClone();
        _template.Remove("choices");
        _template.Remove("usage");
        if (data["usage"] is JsonObject usage)
        {
            _usage = (JsonObject)usage.DeepClone();
        }
    }

    private List<SseEvent> Pass(JsonObject data, JsonArray choices)
    {
        var passed = new List<SseEvent>();
        foreach (var choice in choices.OfType<JsonObject>())
        {
            if (choice["index"] is JsonValue index && index.TryGetValue<int>(out var number) && number != 0)
            {
                _split = true;
                continue;
            }

            var delta = choice["delta"] as JsonObject ?? [];
            Fold(delta);
            if (choice["finish_reason"] is JsonValue reason && reason.TryGetValue<string>(out var finish))
            {
                FinishReason = finish;
            }

            var outgoing = (JsonObject)delta.DeepClone();
            outgoing.Remove("tool_calls");
            if (dropRole)
            {
                outgoing.Remove("role");
            }

            if (outgoing.Count == 0)
            {
                continue;
            }

            var kept = (JsonObject)choice.DeepClone();
            kept["delta"] = outgoing;
            kept["finish_reason"] = null;
            kept.Remove("native_finish_reason");
            var chunk = (JsonObject)data.DeepClone();
            chunk.Remove("usage");
            chunk["id"] = ClientId;
            chunk["choices"] = new JsonArray(kept);
            passed.Add(Unnamed(chunk.ToJsonString(MessagesApi.Json)));
        }

        return passed;
    }

    private void Fold(JsonObject delta)
    {
        if (delta["content"] is JsonValue content && content.TryGetValue<string>(out var text))
        {
            _text.Append(text);
        }

        if (delta["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls.OfType<JsonObject>())
            {
                Hold(call);
            }
        }

        foreach (var (name, value) in delta.Where(static pair => !Folded.Contains(pair.Key)))
        {
            Extend(name, value);
        }
    }

    private void Hold(JsonObject delta)
    {
        var index = delta["index"] is JsonValue position && position.TryGetValue<int>(out var number) ? number : _calls.Count;
        if (!_calls.TryGetValue(index, out var call))
        {
            _calls[index] = call = new Call();
        }

        if (delta["id"] is JsonValue id && id.TryGetValue<string>(out var callId) && call.Id.Length == 0)
        {
            call.Id = callId;
        }

        if (delta["function"] is not JsonObject function)
        {
            return;
        }

        if (function["name"] is JsonValue name && name.TryGetValue<string>(out var functionName) && call.Name.Length == 0)
        {
            call.Name = functionName;
        }

        if (function["arguments"] is JsonValue arguments && arguments.TryGetValue<string>(out var fragment))
        {
            call.Arguments.Append(fragment);
        }
    }

    private void Extend(string name, JsonNode? value)
    {
        switch (value)
        {
            case JsonArray items:
                var all = _extras[name] as JsonArray ?? [];
                foreach (var item in items)
                {
                    all.Add(item?.DeepClone());
                }

                _extras[name] = all;
                break;
            case JsonValue text when text.TryGetValue<string>(out var fragment):
                _extras[name] = (_extras[name] is JsonValue before && before.TryGetValue<string>(out var earlier) ? earlier : "") + fragment;
                break;
            default:
                _extras[name] = value?.DeepClone();
                break;
        }
    }

    private JsonObject Assistant()
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = _text.Length > 0 ? _text.ToString() : null };
        foreach (var (name, value) in _extras)
        {
            message[name] = value?.DeepClone();
        }

        message["tool_calls"] = new JsonArray(_calls.Values.Select(static call => (JsonNode)new JsonObject
        {
            ["id"] = call.Id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToString() }
        }).ToArray());
        return message;
    }

    private JsonObject Base()
    {
        var chunk = (JsonObject)_template.DeepClone();
        chunk["id"] = ClientId;
        return chunk;
    }

    private SseEvent Chunk(JsonObject delta, string? finishReason)
    {
        var chunk = Base();
        chunk["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason });
        return Unnamed(chunk.ToJsonString(MessagesApi.Json));
    }

    private JsonObject MergedUsage(long outputTokens)
    {
        var usage = _usage is null ? [] : (JsonObject)_usage.DeepClone();
        usage["completion_tokens"] = outputTokens;
        if (usage["prompt_tokens"] is JsonValue prompt && prompt.TryGetValue<long>(out var promptTokens))
        {
            usage["total_tokens"] = promptTokens + outputTokens;
        }

        return usage;
    }

    private sealed class Call
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public StringBuilder Arguments { get; } = new();
    }
}
