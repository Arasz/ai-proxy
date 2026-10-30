using AiProxy.Configuration;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Providers;

namespace AiProxy.Ccr;

/// <summary>What the proxy does with one provider request.</summary>
public enum CcrAction
{
    /// <summary>Forward it as before.</summary>
    Forward,

    /// <summary>Relay a <c>create</c> or stateless request, with <c>proxy_retrieve</c> appended to its tools.</summary>
    RelayWithTool,

    /// <summary>Relay a <c>continue</c> of a thread created with the tool, pointed at the message the ledger names.</summary>
    RelayContinue,

    /// <summary>Refuse it: the ledger could not be read, so a <c>continue</c> must not go out as sent.</summary>
    Refuse
}

/// <summary>The plan for one provider request; build it with the factories.</summary>
/// <param name="Action">What the proxy does.</param>
/// <param name="Mode">The CCR mode the pipeline previews with.</param>
/// <param name="ContinueFrom">The message a relayed <c>continue</c> follows; empty otherwise.</param>
public sealed record CcrPlan(CcrAction Action, CcrMode Mode, string ContinueFrom)
{
    /// <summary>The only path CCR acts on: the Anthropic Messages API, shared with <see cref="ProviderEndpoints.AnthropicMessagesPath" />.</summary>
    public const string MessagesPath = ProviderEndpoints.AnthropicMessagesPath;

    /// <summary>The body a client gets when the thread ledger cannot be read: an overloaded error, which it retries.</summary>
    public const string LedgerUnavailable = """{"type":"error","error":{"type":"overloaded_error","message":"ai-proxy could not read its thread ledger; retry."}}""";

    /// <summary>Forward as before.</summary>
    public static CcrPlan Forward { get; } = new(CcrAction.Forward, CcrMode.Off, "");

    /// <summary>Why the ledger could not be read; set only on a refusal.</summary>
    public Exception? Failure { get; private init; }

    /// <summary>Relay a thread <c>create</c>.</summary>
    public static CcrPlan Create { get; } = new(CcrAction.RelayWithTool, CcrMode.Thread, "");

    /// <summary>Relay a stateless request.</summary>
    public static CcrPlan Stateless { get; } = new(CcrAction.RelayWithTool, CcrMode.Stateless, "");

    /// <summary>Whether the proxy answers the client itself.</summary>
    public bool Relays => Action is CcrAction.RelayWithTool or CcrAction.RelayContinue;

    /// <summary>Refuse the request because the ledger read failed.</summary>
    public static CcrPlan Refuse(Exception failure)
    {
        return new CcrPlan(CcrAction.Refuse, CcrMode.Off, "") { Failure = failure };
    }

    /// <summary>Relay a thread <c>continue</c> from the message the ledger names.</summary>
    public static CcrPlan Continue(string continueFrom)
    {
        return new CcrPlan(CcrAction.RelayContinue, CcrMode.Thread, continueFrom);
    }

    /// <summary>Decides the plan of a request; a <c>continue</c> is looked up in the ledger whatever the settings, so a thread with hidden turns keeps working.</summary>
    public static async Task<CcrPlan> DecideAsync(
        CcrSettings settings,
        IOriginalStore store,
        Dialect dialect,
        string method,
        string path,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (dialect != Dialect.Anthropic || !HttpMethods.IsPost(method) || path != MessagesPath || CcrRequest.Read(body) is not { Response: CcrResponse.Streamed } request)
        {
            return Forward;
        }

        return request.Thread switch
        {
            CcrThread.Continue when request.PreviousMessageId is { } previous => await LookUpAsync(store, previous, cancellationToken).ConfigureAwait(false),
            CcrThread.Create when settings.Enabled && request.Tools == CcrTools.Defined => Create,
            CcrThread.None when settings.Enabled && request.Tools == CcrTools.Defined => Stateless,
            _ => Forward
        };
    }

    private static async Task<CcrPlan> LookUpAsync(IOriginalStore store, string previous, CancellationToken cancellationToken)
    {
        try
        {
            return await store.FindThreadAsync(previous, cancellationToken).ConfigureAwait(false) is { } continueFrom
                ? Continue(continueFrom)
                : Forward;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Refuse(exception);
        }
    }
}