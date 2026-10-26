namespace AiProxy.Core.Content.Handlers;

/// <summary>Changes nothing, and is what a kind no handler claims falls back to.</summary>
public sealed class PassThroughCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "unknown.pass-through";

    /// <inheritdoc />
    public bool CanHandle(ContentType type)
    {
        return type == ContentType.Unknown;
    }

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        return ValueTask.FromResult(new HandledContent(part.Text, HandlerPath));
    }
}