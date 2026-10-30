namespace AiProxy.Core.Content;

/// <summary>Routes a part to its reducer, runs it, and measures what it saved.</summary>
/// <param name="router">The router that picks the reducer.</param>
public sealed class CompressionExecutor(IContentRouter router) : ICompressionExecutor
{
    /// <inheritdoc />
    public async ValueTask<CompressedPart> ExecuteAsync(ContentPart part, CancellationToken cancellationToken)
    {
        var route = router.Route(part);
        var handled = await route.Handler.HandleAsync(part, cancellationToken).ConfigureAwait(false);
        var ratio = part.Text.Length == 0 ? 1.0 : (double)handled.Text.Length / part.Text.Length;

        return new CompressedPart(part.ChunkId, part.Type, handled.Text, part.Text, handled.Path, ratio, handled.RetrievalId);
    }
}
