namespace AiProxy.Core.Content;

/// <summary>Which handler runs on a part, and why.</summary>
/// <param name="Type">The kind of the part that was routed.</param>
/// <param name="Handler">The handler that claims the kind.</param>
/// <param name="Reason">The evidence behind the choice.</param>
public sealed record ContentRoute(ContentType Type, ICompressionHandler Handler, string Reason);

/// <summary>Picks the handler that runs on a part.</summary>
public interface IContentRouter
{
    /// <summary>Routes the part to a handler.</summary>
    /// <param name="part">The part to route.</param>
    ContentRoute Route(ContentPart part);
}