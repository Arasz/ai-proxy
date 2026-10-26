namespace AiProxy.Core.Content.Handlers;

/// <summary>Drops the blank lines and the whole-line comments of a configuration file.</summary>
public sealed class TomlCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "toml.strip-comments";

    /// <inheritdoc />
    public bool CanHandle(ContentType type)
    {
        return type == ContentType.Toml;
    }

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        var kept = new List<string>();
        foreach (var segment in LiteralBlocks.TomlStrings(part.Text))
        {
            var line = segment.Text.TrimEnd('\n');

            if (segment.IsLiteral)
            {
                kept.Add(line);
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            kept.Add(line);
        }

        return ValueTask.FromResult(new HandledContent(string.Join("\n", kept), HandlerPath));
    }
}