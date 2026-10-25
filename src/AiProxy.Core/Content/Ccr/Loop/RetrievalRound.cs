using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>
/// One upstream response inside a client response. Text, thinking and server blocks pass on as they
/// arrive under the client's block numbering; <c>tool_use</c> blocks, <c>message_delta</c> and
/// <c>message_stop</c> are held until the loop decides. The assistant content is rebuilt as received.
/// </summary>
/// <param name="first">Whether this is the first round, the only one whose <c>message_start</c> the client sees.</param>
/// <param name="nextIndex">The client block index the first block passed on gets.</param>
public sealed class RetrievalRound(bool first, int nextIndex)
{
    private readonly Dictionary<int, Block> _blocks = [];
    private readonly List<Block> _order = [];
    private int _nextIndex = nextIndex;

    /// <summary>The response's message id, from <c>message_start</c>.</summary>
    public string? MessageId { get; private set; }

    /// <summary>The stop reason from <c>message_delta</c>.</summary>
    public string? StopReason { get; private set; }

    /// <summary>The output tokens this response reported.</summary>
    public long OutputTokens { get; private set; }

    /// <summary>The held <c>message_delta</c> data.</summary>
    public JsonObject? Delta { get; private set; }

    /// <summary>The held <c>message_delta</c> event as received.</summary>
    public SseEvent? DeltaEvent { get; private set; }

    /// <summary>The held <c>message_stop</c> event as received.</summary>
    public SseEvent? StopEvent { get; private set; }

    /// <summary>Whether <c>message_stop</c> arrived.</summary>
    public bool Ended { get; private set; }

    /// <summary>Whether the upstream sent an <c>error</c> event, which was passed on.</summary>
    public bool Failed { get; private set; }

    /// <summary>Whether a delta could not be folded into its block, so the content cannot be re-sent as received.</summary>
    public bool Lossy { get; private set; }

    /// <summary>The next client index after the blocks passed on so far.</summary>
    public int NextIndex => _nextIndex;

    /// <summary>The tool calls of this response, in order.</summary>
    public IReadOnlyList<ToolCall> ToolCalls => _order
        .Where(static block => block.Held)
        .Select(static block => new ToolCall(
            block.Content["id"]?.GetValue<string>() ?? "",
            block.Content["name"]?.GetValue<string>() ?? "",
            block.Content["input"]?.ToJsonString() ?? "{}"))
        .ToArray();

    /// <summary>The assistant content as received, for a stateless follow-up.</summary>
    public JsonArray Content() => new(_order.Select(static block => (JsonNode)block.Content.DeepClone()).ToArray());

    /// <summary>Takes one upstream event and returns what the client gets now.</summary>
    public IReadOnlyList<SseEvent> Accept(SseEvent sse)
    {
        var data = JsonNode.Parse(sse.Data) as JsonObject;
        switch (sse.Name)
        {
            case "message_start":
                MessageId = data?["message"]?["id"]?.GetValue<string>();
                return first ? [sse] : [];
            case "content_block_start":
                return Start(sse, data!);
            case "content_block_delta":
            case "content_block_stop":
                return Continue(sse, data!);
            case "message_delta":
                Delta = data;
                DeltaEvent = sse;
                StopReason = data?["delta"]?["stop_reason"]?.GetValue<string>();
                OutputTokens = data?["usage"]?["output_tokens"]?.GetValue<long>() ?? 0;
                return [];
            case "message_stop":
                Ended = true;
                StopEvent = sse;
                return [];
            case "error":
                Failed = true;
                return [sse];
            default:
                return [sse];
        }
    }

    /// <summary>Returns the held events of the tool calls the client should see, renumbered from <paramref name="index"/>.</summary>
    public IReadOnlyList<SseEvent> Release(ref int index, bool includeRetrievals)
    {
        var released = new List<SseEvent>();
        foreach (var block in _order.Where(static block => block.Held))
        {
            if (!includeRetrievals && block.Content["name"]?.GetValue<string>() == CcrRetrieval.Name)
            {
                continue;
            }

            var clientIndex = index++;
            released.AddRange(block.Events.Select(held => Renumber(held, block.Index, clientIndex)));
        }

        return released;
    }

    private IReadOnlyList<SseEvent> Start(SseEvent sse, JsonObject data)
    {
        var index = data["index"]!.GetValue<int>();
        var content = data["content_block"]!.AsObject().DeepClone().AsObject();
        var block = new Block(index, content, content["type"]?.GetValue<string>() == "tool_use");
        _blocks[index] = block;
        _order.Add(block);
        if (block.Held)
        {
            block.Events.Add(sse);
            return [];
        }

        block.ClientIndex = _nextIndex++;
        return [Renumber(sse, index, block.ClientIndex)];
    }

    private IReadOnlyList<SseEvent> Continue(SseEvent sse, JsonObject data)
    {
        var index = data["index"]!.GetValue<int>();
        if (!_blocks.TryGetValue(index, out var block))
        {
            return [sse];
        }

        if (sse.Name == "content_block_delta")
        {
            Fold(block, data["delta"]!.AsObject());
        }
        else
        {
            Close(block);
        }

        if (block.Held)
        {
            block.Events.Add(sse);
            return [];
        }

        return [Renumber(sse, index, block.ClientIndex)];
    }

    private void Fold(Block block, JsonObject delta)
    {
        var content = block.Content;
        switch (delta["type"]?.GetValue<string>())
        {
            case "text_delta":
                Append(content, "text", delta["text"]);
                break;
            case "thinking_delta":
                Append(content, "thinking", delta["thinking"]);
                break;
            case "signature_delta":
                Append(content, "signature", delta["signature"]);
                break;
            case "input_json_delta":
                block.PartialJson.Append(delta["partial_json"]?.GetValue<string>());
                break;
            case "citations_delta":
                if (content["citations"] is not JsonArray citations)
                {
                    content["citations"] = citations = [];
                }

                citations.Add(delta["citation"]?.DeepClone());
                break;
            default:
                Lossy = true;
                break;
        }
    }

    private void Close(Block block)
    {
        if (block.PartialJson.Length == 0)
        {
            return;
        }

        try
        {
            block.Content["input"] = JsonNode.Parse(block.PartialJson.ToString());
        }
        catch (JsonException)
        {
            Lossy = true;
        }
    }

    private static void Append(JsonObject content, string name, JsonNode? addition)
    {
        content[name] = (content[name]?.GetValue<string>() ?? "") + (addition?.GetValue<string>() ?? "");
    }

    private static SseEvent Renumber(SseEvent sse, int upstreamIndex, int clientIndex)
    {
        if (upstreamIndex == clientIndex)
        {
            return sse;
        }

        var data = JsonNode.Parse(sse.Data)!.AsObject();
        data["index"] = clientIndex;
        return sse with { Data = data.ToJsonString(RetrievalJsonOptions.Relaxed) };
    }

    private sealed class Block(int index, JsonObject content, bool held)
    {
        public int Index { get; } = index;

        public JsonObject Content { get; } = content;

        public bool Held { get; } = held;

        public int ClientIndex { get; set; }

        public List<SseEvent> Events { get; } = [];

        public StringBuilder PartialJson { get; } = new();
    }
}
