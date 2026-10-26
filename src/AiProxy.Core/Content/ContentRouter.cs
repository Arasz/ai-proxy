namespace AiProxy.Core.Content;

/// <summary>Asks each handler in turn and takes the first that claims the kind.</summary>
public sealed class ContentRouter : IContentRouter
{
    private readonly ICompressionHandler _fallback;
    private readonly IReadOnlyList<ICompressionHandler> _handlers;

    /// <summary>Holds the handlers, in the order they are asked, and the fallback.</summary>
    /// <param name="handlers">The handlers to ask, most specific first.</param>
    /// <param name="fallback">The handler that runs when nobody claims the kind.</param>
    public ContentRouter(IEnumerable<ICompressionHandler> handlers, ICompressionHandler fallback)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(fallback);

        _handlers = [.. handlers];
        _fallback = fallback;
    }

    /// <inheritdoc />
    public ContentRoute Route(ContentPart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        foreach (var handler in _handlers)
            if (handler.CanHandle(part.Type))
                return new ContentRoute(part.Type, handler, $"the {handler.GetType().Name} claims {part.Type}");

        return new ContentRoute(part.Type, _fallback, $"no handler claims {part.Type}, so the fallback runs");
    }
}