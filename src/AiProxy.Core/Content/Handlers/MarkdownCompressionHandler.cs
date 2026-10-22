using System.Text.RegularExpressions;

namespace AiProxy.Core.Content.Handlers;

/// <summary>Drops the hidden comments of a Markdown document and the runs of empty lines.</summary>
public sealed class MarkdownCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "markdown.strip-comments";

    private static readonly Regex CommentPattern = new(
        @"<!--.*?-->",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex BlankRunPattern = new(
        @"(?:\r?\n){3,}",
        RegexOptions.Compiled);

    /// <inheritdoc />
    public bool CanHandle(ContentType type) => type == ContentType.Markdown;

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        var reduced = BlankRunPattern.Replace(CommentPattern.Replace(part.Text, string.Empty), "\n\n").Trim();

        return ValueTask.FromResult(new HandledContent(reduced, HandlerPath));
    }
}
