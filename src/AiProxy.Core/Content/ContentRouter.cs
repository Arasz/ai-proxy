namespace AiProxy.Core.Content;

/// <summary>Asks each handler in turn and takes the first that claims the kind.</summary>
/// <param name="handlers">The handlers to ask, most specific first.</param>
/// <param name="fallback">The handler that runs when nobody claims the kind.</param>
public sealed class ContentRouter(IEnumerable<ICompressionHandler> handlers, ICompressionHandler fallback) : IContentRouter
{
    private readonly IReadOnlyList<ICompressionHandler> _handlers = [.. handlers];

    /// <inheritdoc />
    public ContentRoute Route(ContentPart part)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(part.Type))
            {
                return new ContentRoute(part.Type, handler, $"the {handler.GetType().Name} claims {part.Type}");
            }
        }

        return new ContentRoute(part.Type, fallback, $"no handler claims {part.Type}, so the fallback runs");
    }
}
