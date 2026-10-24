using System.Text;
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

        // The comment goes from the prose and not from the code, so the document is
        // taken apart at the fences first: a comment that opens in the prose and closes
        // in a fence is left alone rather than half removed.
        var builder = new StringBuilder(part.Text.Length);

        foreach (var segment in LiteralBlocks.MarkdownFences(part.Text))
        {
            builder.Append(segment.IsLiteral ? segment.Text : CommentPattern.Replace(segment.Text, ""));
        }

        var reduced = BlankRunPattern.Replace(builder.ToString(), "\n\n").Trim();

        return ValueTask.FromResult(new HandledContent(reduced, HandlerPath));
    }
}
