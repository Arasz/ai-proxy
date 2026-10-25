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
    Refuse,
}

/// <summary>The plan for one provider request; build it with the factories.</summary>
/// <param name="Action">What the proxy does.</param>
/// <param name="Mode">The CCR mode the pipeline previews with.</param>
/// <param name="ContinueFrom">The message a relayed <c>continue</c> follows; empty otherwise.</param>
public sealed record CcrPlan(CcrAction Action, CcrMode Mode, string ContinueFrom)
{
    /// <summary>The path of the Messages API.</summary>
    public const string MessagesPath = "/v1/messages";

    /// <summary>The body a client gets when the thread ledger cannot be read: an overloaded error, which it retries.</summary>
    public const string LedgerUnavailable = """{"type":"error","error":{"type":"overloaded_error","message":"ai-proxy could not read its thread ledger; retry."}}""";

    /// <summary>Forward as before.</summary>
    public static CcrPlan Forward { get; } = new(CcrAction.Forward, CcrMode.Off, "");

    /// <summary>Refuse the request.</summary>
    public static CcrPlan Refuse { get; } = new(CcrAction.Refuse, CcrMode.Off, "");

    /// <summary>Relay a thread <c>create</c>.</summary>
    public static CcrPlan Create { get; } = new(CcrAction.RelayWithTool, CcrMode.Thread, "");

    /// <summary>Relay a stateless request.</summary>
    public static CcrPlan Stateless { get; } = new(CcrAction.RelayWithTool, CcrMode.Stateless, "");

    /// <summary>Relay a thread <c>continue</c> from the message the ledger names.</summary>
    public static CcrPlan Continue(string continueFrom) => new(CcrAction.RelayContinue, CcrMode.Thread, continueFrom);

    /// <summary>Whether the proxy answers the client itself.</summary>
    public bool Relays => Action is CcrAction.RelayWithTool or CcrAction.RelayContinue;

    /// <summary>Whether the ledger could not be read.</summary>
    public bool LedgerUnreadable => Action == CcrAction.Refuse;

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
        if (dialect != Dialect.Anthropic || method != "POST" || path != MessagesPath || CcrRequest.Read(body) is not { } request || !request.Stream)
        {
            return Forward;
        }

        return request.Thread switch
        {
            CcrThread.Continue when request.PreviousMessageId is { } previous => await LookUpAsync(store, previous, cancellationToken).ConfigureAwait(false),
            CcrThread.Create when settings.Enabled && request.HasTools => Create,
            CcrThread.None when settings.Enabled && request.HasTools => Stateless,
            _ => Forward,
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
            return Refuse;
        }
    }
}
