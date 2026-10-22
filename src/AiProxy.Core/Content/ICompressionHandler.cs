namespace AiProxy.Core.Content;

/// <summary>Reduces one kind of content, and says which path produced the result.</summary>
public interface ICompressionHandler
{
    /// <summary>Whether this handler handles the kind.</summary>
    /// <param name="type">The kind of a part.</param>
    bool CanHandle(ContentType type);

    /// <summary>Reduces the part, or leaves it alone.</summary>
    /// <param name="part">The part to reduce.</param>
    /// <param name="cancellationToken">The token that stops the work.</param>
    ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken);
}
