namespace AiProxy.Core.Content.Handlers;

/// <summary>Leaves a directory listing alone, because nothing compresses it without losing names.</summary>
public sealed class DirectoryListingCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "listing.pass-through";

    /// <inheritdoc />
    public bool CanHandle(ContentType type) => type == ContentType.DirectoryListing;

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        return ValueTask.FromResult(new HandledContent(part.Text, HandlerPath));
    }
}
