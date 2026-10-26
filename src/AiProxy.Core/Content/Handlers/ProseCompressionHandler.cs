namespace AiProxy.Core.Content.Handlers;

/// <summary>Drops the lines a piece of prose repeats, and re-attaches what the drop lost.</summary>
public sealed class ProseCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "prose.dedupe";

    /// <inheritdoc />
    public bool CanHandle(ContentType type)
    {
        return type == ContentType.Prose;
    }

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        var reduced = Reduction.DeduplicateLines(part.Text);
        var overlaid = MustKeepOverlay.Apply(reduced, part.Text);

        return ValueTask.FromResult(new HandledContent(overlaid.Text, HandlerPath));
    }
}