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
/// <param name="prior">The usage of the rounds before this one, summed.</param>
public sealed class ChatRound(bool dropRole, bool clientUsage, string clientId, JsonObject? prior = null) : IRetrievalRound
{
    private const string Done = "[DONE]";
    private const string ToolCallsFinish = "tool_calls";
    private const string StopFinish = "stop";
    private const string ReasoningDetails = "reasoning_details";
    private static readonly string[] Folded = ["role", "content", "tool_calls"];

    private readonly List<Call> _calls = [];
    private readonly Dictionary<int, Call> _open = [];
    private readonly StringBuilder _text = new();
    private readonly JsonObject _extras = [];
    private JsonObject _template = [];
    private JsonObject? _usage;
    private bool _split;
    private int _lastIndex;

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
    public bool Lossy => _split || _calls.Any(static call => call.Id.Length == 0 || call.Name.Length == 0);

    /// <inheritdoc />
    public bool StopsForTools => _calls.Count > 0 && FinishReason is ToolCallsFinish or StopFinish;

    /// <inheritdoc />
    public long OutputTokens => _usage?["completion_tokens"] is JsonValue value && value.TryGetValue<long>(out var tokens) ? tokens : 0;

    /// <inheritdoc />
    public IReadOnlyList<ToolCall> ToolCalls => _calls.OrderBy(static call => call.Index).Select(static call => new ToolCall(call.Id, call.Name, call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString())).ToArray();

    /// <inheritdoc />
    public SseEvent KeepAlive => SseEvent.CommentLine;

    /// <inheritdoc />
    public SseEvent? Terminator => Unnamed(Done);

    /// <summary>The round of the first upstream response.</summary>
    public static ChatRound First(bool clientUsage) => new(false, clientUsage, "");

    /// <inheritdoc />
    public IReadOnlyList<SseEvent> Accept(SseEvent sse)
    {
        if (sse.Name == SseEvent.Comment)
        {
            return [KeepAlive];
        }

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
            if (data["id"] is not null && ClientId.Length > 0)
            {
                data["id"] = ClientId;
            }

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
            chunk["usage"] = MergedUsage();
            events.Add(Unnamed(chunk.ToJsonString(MessagesApi.Json)));
        }

        events.Add(Terminator!);
        return events;
    }

    /// <inheritdoc />
    public IRetrievalRound Next() => new ChatRound(true, clientUsage, ClientId, MergedUsage());

    /// <inheritdoc />
    public byte[] FollowUp(ReadOnlySpan<byte> forwarded, IReadOnlyList<ToolResult> results)
    {
        if (JsonNode.Parse(forwarded) is not JsonObject request || request["messages"] is not JsonArray messages)
        {
            throw new InvalidOperationException("The forwarded request has no messages array to continue.");
        }

        messages.Add(Assistant());
        foreach (var result in results)
        {
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.ToolUseId, ["content"] = result.Content });
        }

        return JsonSerializer.SerializeToUtf8Bytes(request, MessagesApi.Json);
    }

    /// <inheritdoc />
    public SseEvent Error(string message) => Unnamed(new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = MessagesApi.Errors.Api, ["code"] = MessagesApi.Errors.Api } }.ToJsonString(MessagesApi.Json));

    /// <inheritdoc />
    public SseEvent ErrorOf(string body) => Unnamed(body);

    private static SseEvent Unnamed(string data) => new(SseEvent.Unnamed, data);

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
        var index = delta["index"] is JsonValue position && position.TryGetValue<int>(out var number) ? number : _lastIndex;
        _lastIndex = index;
        var id = delta["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var callId) ? callId : "";
        if (!_open.TryGetValue(index, out var call) || (id.Length > 0 && call.Id.Length > 0 && call.Id != id))
        {
            call = new Call(index);
            _calls.Add(call);
            _open[index] = call;
        }

        if (call.Id.Length == 0)
        {
            call.Id = id;
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
            case null:
                return;
            case JsonArray items:
                if (_extras[name] is not JsonArray all)
                {
                    all = [];
                    _extras[name] = all;
                }

                foreach (var item in items)
                {
                    if (name == ReasoningDetails && item is JsonObject piece && SameIndex(all, piece) is { } joined)
                    {
                        Join(joined, piece);
                    }
                    else
                    {
                        all.Add(item?.DeepClone());
                    }
                }

                break;
            case JsonValue text when text.TryGetValue<string>(out var fragment):
                _extras[name] = (_extras[name] is JsonValue before && before.TryGetValue<string>(out var earlier) ? earlier : "") + fragment;
                break;
            default:
                _extras[name] = value.DeepClone();
                break;
        }
    }

    private static JsonObject? SameIndex(JsonArray all, JsonObject piece) =>
        piece["index"] is JsonValue index && index.TryGetValue<int>(out var number)
            ? all.OfType<JsonObject>().FirstOrDefault(known => known["index"] is JsonValue other && other.TryGetValue<int>(out var otherNumber) && otherNumber == number)
            : null;

    private static void Join(JsonObject into, JsonObject piece)
    {
        foreach (var (name, value) in piece)
        {
            if (value is null || name == "index")
            {
                continue;
            }

            into[name] = name == "text" && value is JsonValue text && text.TryGetValue<string>(out var fragment) && into["text"] is JsonValue before && before.TryGetValue<string>(out var earlier)
                ? earlier + fragment
                : value.DeepClone();
        }
    }

    private JsonObject Assistant()
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = _text.Length > 0 ? _text.ToString() : null };
        foreach (var (name, value) in _extras)
        {
            message[name] = value?.DeepClone();
        }

        message["tool_calls"] = new JsonArray(_calls.OrderBy(static call => call.Index).Select(static call => (JsonNode)new JsonObject
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

    private JsonObject MergedUsage()
    {
        var total = prior is null ? [] : (JsonObject)prior.DeepClone();
        if (_usage is not null)
        {
            Sum(total, _usage);
        }

        return total;
    }

    private static void Sum(JsonObject total, JsonObject part)
    {
        foreach (var (name, value) in part)
        {
            switch (value)
            {
                case null:
                    break;
                case JsonObject nested:
                    if (total[name] is not JsonObject inner)
                    {
                        inner = [];
                        total[name] = inner;
                    }

                    Sum(inner, nested);
                    break;
                case JsonValue number when number.GetValueKind() == JsonValueKind.Number && number.TryGetValue<decimal>(out var amount):
                    total[name] = (total[name] is JsonValue before && before.TryGetValue<decimal>(out var earlier) ? earlier : 0m) + amount;
                    break;
                default:
                    total[name] = value.DeepClone();
                    break;
            }
        }
    }

    private sealed class Call(int index)
    {
        public int Index { get; } = index;

        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public StringBuilder Arguments { get; } = new();
    }
}
