namespace AiProxy.Core.Content.Ccr;

/// <summary>Decides whether a region is sent as a preview, and keeps its original first when the mode needs it.</summary>
/// <param name="store">Where a thread's originals are kept.</param>
/// <param name="builders">Makes a preview builder for one region.</param>
/// <param name="options">The CCR numbers.</param>
public sealed class CcrPreviewer(IOriginalStore store, Func<CcrPreviewBuilder> builders, CcrOptions options)
{
    /// <summary>How long a thread's original may take to be kept before the region goes out without a preview.</summary>
    public static readonly TimeSpan KeepTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The CCR numbers.</summary>
    public CcrOptions Options => options;

    /// <summary>Returns the preview to send, or null to send the reduced text; throws when a thread's original could not be kept.</summary>
    /// <param name="original">The region's text as the client sent it.</param>
    /// <param name="reduced">The region's text after its reducers.</param>
    /// <param name="mode">The request's CCR mode.</param>
    /// <param name="cancellationToken">The request's token.</param>
    public async ValueTask<CcrPreview?> PreviewAsync(string original, string reduced, CcrMode mode, CancellationToken cancellationToken)
    {
        if (mode == CcrMode.Off || options.TokensOf(reduced.Length) < options.MinimumTokens) return null;

        var preview = builders().Build(original, options);
        if (preview is null || mode != CcrMode.Thread) return preview;

        await KeepAsync(preview.Id, original, mode, cancellationToken).ConfigureAwait(false);
        return preview;
    }

    /// <summary>Keeps the original under its id in the store when the mode is a thread; throws when it could not be kept.</summary>
    /// <param name="id">The id the sent text names.</param>
    /// <param name="original">The text as the client sent it.</param>
    /// <param name="mode">The request's CCR mode; stateless originals travel with the request instead.</param>
    /// <param name="cancellationToken">The request's token.</param>
    public async ValueTask KeepAsync(string id, string original, CcrMode mode, CancellationToken cancellationToken)
    {
        if (mode != CcrMode.Thread) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(KeepTimeout);
        await store.KeepAsync(new KeptOriginal(id, original, CcrText.LinesOf(original).Length), timeout.Token).ConfigureAwait(false);
    }
}