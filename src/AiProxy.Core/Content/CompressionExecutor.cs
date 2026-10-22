namespace AiProxy.Core.Content;

/// <summary>Routes a part to its reducer, runs it, and measures what it saved.</summary>
public sealed class CompressionExecutor : ICompressionExecutor
{
    private readonly IContentRouter _router;

    /// <summary>Holds the router that picks the reducer.</summary>
    /// <param name="router">The router that picks the reducer.</param>
    public CompressionExecutor(IContentRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);

        _router = router;
    }

    /// <inheritdoc />
    public async ValueTask<CompressedPart> ExecuteAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        var route = _router.Route(part);
        var handled = await route.Handler.HandleAsync(part, cancellationToken).ConfigureAwait(false);
        var ratio = part.Text.Length == 0 ? 1.0 : (double)handled.Text.Length / part.Text.Length;

        return new CompressedPart(part.ChunkId, part.Type, handled.Text, handled.Path, ratio);
    }
}
