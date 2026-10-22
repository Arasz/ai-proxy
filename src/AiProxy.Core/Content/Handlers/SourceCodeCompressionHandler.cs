namespace AiProxy.Core.Content.Handlers;

/// <summary>Drops the blank lines of a source file, and keeps every other line whole.</summary>
public sealed class SourceCodeCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "sourcecode.blank-lines";

    /// <inheritdoc />
    public bool CanHandle(ContentType type) => type == ContentType.SourceCode;

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        var kept = new List<string>();
        foreach (var line in part.Text.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            kept.Add(line);
        }

        return ValueTask.FromResult(new HandledContent(string.Join("\n", kept), HandlerPath));
    }
}
