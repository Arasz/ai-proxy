using System.Text.Json.Nodes;
using static AiProxy.Core.Content.Ccr.MessagesApi;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>Where a client response is in the retrieval loop.</summary>
public enum RetrievalState
{
    /// <summary>An upstream response is streaming to the client.</summary>
    Forwarding,

    /// <summary>The upstream response ended; its tool calls are held while the loop decides.</summary>
    HoldingTools,

    /// <summary>The proxy is answering <c>proxy_retrieve</c> calls and sending the follow-up.</summary>
    Retrieving,

    /// <summary>The message is complete and ready to release.</summary>
    Done,

    /// <summary>The client got an error event.</summary>
    Failed
}

/// <summary>One move of the retrieval loop and what triggered it.</summary>
/// <param name="From">The state it left.</param>
/// <param name="To">The state it entered.</param>
/// <param name="Trigger">What made it move.</param>
public sealed record RetrievalMove(RetrievalState From, RetrievalState To, string Trigger);

/// <summary>What the host does after an upstream response ended.</summary>
public enum RetrievalStep
{
    /// <summary>Write <see cref="RetrievalLoop.Finish" /> and stop.</summary>
    Release,

    /// <summary>Answer the round's calls with <see cref="RetrievalLoop.ToolResults" /> and send a follow-up.</summary>
    Retrieve,

    /// <summary>Write <see cref="RetrievalLoop.Failure" /> and stop.</summary>
    Fail
}

/// <summary>
///     The retrieval loop of one client response: the declared state transitions, the rounds, and the
///     merge of every upstream response into the one message the client sees.
/// </summary>
public sealed class RetrievalLoop
{
    /// <summary>How many rounds of <c>proxy_retrieve</c> calls are answered with text.</summary>
    public const int MaximumRounds = 3;

    /// <summary>What a client call made next to <c>proxy_retrieve</c> gets instead of running.</summary>
    public const string DeferredCall = "Not run: this call was made together with proxy_retrieve. Read the retrieved text, then issue this call again.";

    private static readonly Dictionary<RetrievalState, RetrievalState[]> Transitions = new()
    {
        [RetrievalState.Forwarding] = [RetrievalState.HoldingTools, RetrievalState.Failed],
        [RetrievalState.HoldingTools] = [RetrievalState.Retrieving, RetrievalState.Done, RetrievalState.Failed],
        [RetrievalState.Retrieving] = [RetrievalState.Forwarding, RetrievalState.Failed],
        [RetrievalState.Done] = [RetrievalState.Failed],
        [RetrievalState.Failed] = []
    };

    private readonly List<RetrievalMove> _history = [];
    private long _outputTokens;

    /// <summary>The current state.</summary>
    public RetrievalState State { get; private set; } = RetrievalState.Forwarding;

    /// <summary>Every move the loop made, with what triggered it.</summary>
    public IReadOnlyList<RetrievalMove> History => _history;

    /// <summary>The upstream response streaming now.</summary>
    public RetrievalRound Round { get; private set; } = RetrievalRound.First();

    /// <summary>How many rounds of calls were answered.</summary>
    public int Answered { get; private set; }

    /// <summary>The message id of the first upstream response: the one the client saw; empty until it ended.</summary>
    public string FirstMessageId { get; private set; } = "";

    /// <summary>Whether the round being answered gets <c>limit_reached</c> instead of text.</summary>
    public bool AtLimit => Answered > MaximumRounds;

    /// <summary>Decides what follows the current round, once its upstream response ended or failed.</summary>
    public RetrievalStep Decide()
    {
        if (FirstMessageId.Length == 0) FirstMessageId = Round.MessageId;

        if (Round.Failed || !Round.Ended)
        {
            Move(RetrievalState.Failed, Round.Failed ? Triggers.UpstreamError : Triggers.CutOff);
            return RetrievalStep.Fail;
        }

        Move(RetrievalState.HoldingTools, Triggers.MessageStop);
        _outputTokens += Round.OutputTokens;
        var retrieves = Round.StopReason == StopReasons.ToolUse && Round.ToolCalls.Any(static call => call.IsRetrieval);
        if (!retrieves)
        {
            Move(RetrievalState.Done, Triggers.NoRetrieval);
            return RetrievalStep.Release;
        }

        if (Answered > MaximumRounds)
        {
            Move(RetrievalState.Failed, Triggers.AfterLimit);
            return RetrievalStep.Fail;
        }

        Answered++;
        Move(RetrievalState.Retrieving, Answered > MaximumRounds ? Triggers.OverLimit : Triggers.Retrieval);
        return RetrievalStep.Retrieve;
    }

    /// <summary>The tool results of the round being answered: retrievals from <paramref name="answers" />, every other call deferred.</summary>
    public IReadOnlyList<ToolResult> ToolResults(IReadOnlyDictionary<string, CcrRetrieval> answers)
    {
        return Round.ToolCalls
            .Select(call => call.IsRetrieval ? ToolResult.Of(call.Id, answers[call.Id]) : ToolResult.Deferred(call.Id))
            .ToArray();
    }

    /// <summary>Starts the round of the follow-up response; its <c>message_start</c> is dropped and its blocks continue the numbering.</summary>
    public RetrievalRound NextRound()
    {
        Move(RetrievalState.Forwarding, Triggers.FollowUp);
        Round = Round.Next();
        return Round;
    }

    /// <summary>The held tool calls, the merged <c>message_delta</c> and <c>message_stop</c> of a released message.</summary>
    public IReadOnlyList<SseEvent> Finish()
    {
        var events = new List<SseEvent>(Round.Release());
        events.Add(Answered == 0 && Round.DeltaEvent is { } received ? received : MergedDelta());
        events.Add(Round.StopEvent ?? SseEvent.Of(Events.MessageStop, new JsonObject { [Fields.Type] = Events.MessageStop }));
        return events;
    }

    /// <summary>The last response's <c>message_delta</c> with the output tokens of every response.</summary>
    private SseEvent MergedDelta()
    {
        var delta = Round.Delta?.DeepClone() as JsonObject ?? new JsonObject { [Fields.Type] = Events.MessageDelta, [Fields.Delta] = new JsonObject() };
        if (delta[Fields.Usage] is not JsonObject usage) delta[Fields.Usage] = usage = [];

        usage[Fields.OutputTokens] = _outputTokens;
        return SseEvent.Of(Events.MessageDelta, delta);
    }

    /// <summary>The error event of a failed loop.</summary>
    public SseEvent Failure(string message)
    {
        if (State != RetrievalState.Failed) Move(RetrievalState.Failed, message);

        return SseEvent.Error(Errors.Api, message);
    }

    private void Move(RetrievalState to, string trigger)
    {
        if (!Transitions[State].Contains(to)) throw new InvalidOperationException($"The retrieval loop cannot move from {State} to {to} ({trigger}).");

        _history.Add(new RetrievalMove(State, to, trigger));
        State = to;
    }

    /// <summary>What a move of the loop records as its trigger.</summary>
    private static class Triggers
    {
        public const string UpstreamError = "upstream error event";
        public const string CutOff = "upstream ended without message_stop";
        public const string MessageStop = "message_stop";
        public const string NoRetrieval = "no proxy_retrieve call";
        public const string AfterLimit = "proxy_retrieve called after limit_reached";
        public const string OverLimit = "proxy_retrieve over the limit";
        public const string Retrieval = "proxy_retrieve call";
        public const string FollowUp = "follow-up sent";
    }
}