namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>
///     One upstream response inside a client response, in one provider dialect. The loop drives the
///     state machine through this seam and never reads dialect events itself.
/// </summary>
public interface IRetrievalRound
{
    /// <summary>The response's message id; empty until it arrives.</summary>
    string MessageId { get; }

    /// <summary>Whether the response ended in full.</summary>
    bool Ended { get; }

    /// <summary>Whether the upstream sent an error event, which was passed on.</summary>
    bool Failed { get; }

    /// <summary>Whether the content cannot be re-sent as received.</summary>
    bool Lossy { get; }

    /// <summary>Whether the model stopped to wait for tool results.</summary>
    bool StopsForTools { get; }

    /// <summary>The output tokens this response reported.</summary>
    long OutputTokens { get; }

    /// <summary>The tool calls of this response, in order.</summary>
    IReadOnlyList<ToolCall> ToolCalls { get; }

    /// <summary>Takes one upstream event and returns what the client gets now.</summary>
    IReadOnlyList<SseEvent> Accept(SseEvent sse);

    /// <summary>The held client tool calls and the closing events of a released message.</summary>
    /// <param name="answered">How many rounds of calls were answered before this one.</param>
    /// <param name="outputTokens">The output tokens of every response so far.</param>
    IReadOnlyList<SseEvent> Finish(int answered, long outputTokens);

    /// <summary>The round of the follow-up response.</summary>
    IRetrievalRound Next();

    /// <summary>The follow-up body: the forwarded request continued with this response and the tool results, in the mode the round was made with.</summary>
    byte[] FollowUp(ReadOnlySpan<byte> forwarded, IReadOnlyList<ToolResult> results);

    /// <summary>The error event the client gets when the loop fails.</summary>
    SseEvent Error(string message);

    /// <summary>The error event for an upstream error body that is JSON: the client gets it as the provider wrote it.</summary>
    SseEvent ErrorOf(string body);

    /// <summary>What the client gets while a follow-up has not answered yet.</summary>
    SseEvent KeepAlive { get; }

    /// <summary>The event that ends the client's stream after a failure; null when the dialect has none.</summary>
    SseEvent? Terminator { get; }
}
