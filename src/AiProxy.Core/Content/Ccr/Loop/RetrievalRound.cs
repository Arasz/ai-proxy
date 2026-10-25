using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static AiProxy.Core.Content.Ccr.MessagesApi;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>Whether the client sees a round's <c>message_start</c>.</summary>
public enum MessageStart
{
    /// <summary>Pass it on: the first round.</summary>
    Pass,

    /// <summary>Drop it: a follow-up round continues the client's message.</summary>
    Drop,
}

/// <summary>
/// One upstream response inside a client response. Text, thinking and server blocks pass on as they
/// arrive under the client's block numbering; <c>tool_use</c> blocks, <c>message_delta</c> and
/// <c>message_stop</c> are held until the loop decides. The assistant content is rebuilt as received.
/// </summary>
/// <param name="start">Whether the client sees this round's <c>message_start</c>: only the first round's.</param>
/// <param name="nextIndex">The client block index the first block passed on gets.</param>
public sealed class RetrievalRound(MessageStart start, int nextIndex)
{
    private readonly Dictionary<int, Block> _blocks = [];
    private readonly List<Block> _order = [];
    private int _nextIndex = nextIndex;

    /// <summary>The round of the first upstream response.</summary>
    public static RetrievalRound First() => new(MessageStart.Pass, 0);

    /// <summary>The round of a follow-up response: its <c>message_start</c> is dropped and its blocks continue the numbering.</summary>
    public RetrievalRound Next() => new(MessageStart.Drop, _nextIndex);

    /// <summary>The response's message id, from <c>message_start</c>; empty until it arrives.</summary>
    public string MessageId { get; private set; } = "";

    /// <summary>The stop reason from <c>message_delta</c>; empty until it arrives.</summary>
    public string StopReason { get; private set; } = "";

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
            block.Content.TextOf(Fields.Id),
            block.Content.TextOf(Fields.Name),
            block.Content[Fields.Input]?.ToJsonString() ?? "{}"))
        .ToArray();

    /// <summary>The assistant content as received, for a stateless follow-up.</summary>
    public JsonArray Content() => new(_order.Select(static block => (JsonNode)block.Content.DeepClone()).ToArray());

    /// <summary>Takes one upstream event and returns what the client gets now; an event it cannot read passes on as it came.</summary>
    public IReadOnlyList<SseEvent> Accept(SseEvent sse)
    {
        if (JsonNode.Parse(sse.Data) is not JsonObject data)
        {
            return [sse];
        }

        switch (sse.Name)
        {
            case Events.MessageStart:
                MessageId = data[Fields.Message] is JsonObject message ? message.TextOf(Fields.Id) : "";
                return start == MessageStart.Pass ? [sse] : [];
            case Events.ContentBlockStart:
                return Start(sse, data);
            case Events.ContentBlockDelta:
            case Events.ContentBlockStop:
                return Continue(sse, data);
            case Events.MessageDelta:
                Hold(sse, data);
                return [];
            case Events.MessageStop:
                Ended = true;
                StopEvent = sse;
                return [];
            case Events.Error:
                Failed = true;
                return [sse];
            default:
                return [sse];
        }
    }

    /// <summary>Returns the held events of the client's tool calls, numbered after the blocks passed on; <c>proxy_retrieve</c> calls are never released.</summary>
    public IReadOnlyList<SseEvent> Release()
    {
        var released = new List<SseEvent>();
        var index = _nextIndex;
        foreach (var block in _order.Where(static block => block.Held && block.Content.TextOf(Fields.Name) != CcrRetrieval.Name))
        {
            var clientIndex = index++;
            released.AddRange(block.Events.Select(held => held.Renumbered(block.Index, clientIndex)));
        }

        return released;
    }

    private void Hold(SseEvent sse, JsonObject data)
    {
        Delta = data;
        DeltaEvent = sse;
        StopReason = data[Fields.Delta] is JsonObject delta ? delta.TextOf(Fields.StopReason) : "";
        OutputTokens = data[Fields.Usage] is JsonObject usage && usage[Fields.OutputTokens] is JsonValue tokens && tokens.TryGetValue<long>(out var count) ? count : 0;
    }

    private IReadOnlyList<SseEvent> Start(SseEvent sse, JsonObject data)
    {
        if (data.NumberOf(Fields.Index) is not { } index || data[Fields.ContentBlock] is not JsonObject received)
        {
            return [sse];
        }

        var block = new Block(index, received.DeepClone().AsObject());
        _blocks[index] = block;
        _order.Add(block);
        if (block.Held)
        {
            block.Events.Add(sse);
            return [];
        }

        block.ClientIndex = _nextIndex++;
        return [sse.Renumbered(index, block.ClientIndex)];
    }

    private IReadOnlyList<SseEvent> Continue(SseEvent sse, JsonObject data)
    {
        if (data.NumberOf(Fields.Index) is not { } index || !_blocks.TryGetValue(index, out var block))
        {
            return [sse];
        }

        if (sse.Name == Events.ContentBlockStop)
        {
            Close(block);
        }
        else if (data[Fields.Delta] is JsonObject delta)
        {
            Fold(block, delta);
        }
        else
        {
            Lossy = true;
        }

        if (block.Held)
        {
            block.Events.Add(sse);
            return [];
        }

        return [sse.Renumbered(index, block.ClientIndex)];
    }

    private void Fold(Block block, JsonObject delta)
    {
        var content = block.Content;
        switch (delta.TextOf(Fields.Type))
        {
            case Deltas.Text:
                content.Append(Fields.Text, delta[Fields.Text]);
                break;
            case Deltas.Thinking:
                content.Append(Fields.Thinking, delta[Fields.Thinking]);
                break;
            case Deltas.Signature:
                content.Append(Fields.Signature, delta[Fields.Signature]);
                break;
            case Deltas.InputJson:
                block.PartialJson.Append(delta.TextOf(Fields.PartialJson));
                break;
            case Deltas.Citations:
                if (content[Fields.Citations] is not JsonArray citations)
                {
                    content[Fields.Citations] = citations = [];
                }

                citations.Add(delta[Fields.Citation]?.DeepClone());
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
            block.Content[Fields.Input] = JsonNode.Parse(block.PartialJson.ToString());
        }
        catch (JsonException)
        {
            Lossy = true;
        }
    }

    private sealed class Block(int index, JsonObject content)
    {
        public int Index { get; } = index;

        public JsonObject Content { get; } = content;

        /// <summary>A client tool call, held until the loop decides; every other block passes on.</summary>
        public bool Held { get; } = content.TextOf(Fields.Type) == Blocks.ToolUse;

        public int ClientIndex { get; set; }

        public List<SseEvent> Events { get; } = [];

        public StringBuilder PartialJson { get; } = new();
    }
}
