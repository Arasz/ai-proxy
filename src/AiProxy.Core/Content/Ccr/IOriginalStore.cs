namespace AiProxy.Core.Content.Ccr;

/// <summary>Keeps the originals behind previews and the thread ledger, so a later retrieval and a later <c>continue</c> can find them.</summary>
public interface IOriginalStore
{
    /// <summary>Keeps an original under its preview id; keeping the same id again changes nothing.</summary>
    ValueTask KeepAsync(KeptOriginal original, CancellationToken cancellationToken);

    /// <summary>Returns the original kept under the id, or null when none is kept.</summary>
    ValueTask<string?> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns the message a <c>continue</c> naming this message must follow, or null when the thread was not created with the tool.</summary>
    ValueTask<string?> FindThreadAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>Records that a client which saw <paramref name="messageId" /> continues from <paramref name="continueFrom" />.</summary>
    ValueTask LinkThreadAsync(string messageId, string continueFrom, CancellationToken cancellationToken);
}

/// <summary>An original kept for retrieval.</summary>
/// <param name="Id">The preview id.</param>
/// <param name="Text">The original text.</param>
/// <param name="LineCount">The original's line count.</param>
public sealed record KeptOriginal(string Id, string Text, int LineCount);