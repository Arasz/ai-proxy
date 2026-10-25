using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Code;

namespace AiProxy.Core.Content.Handlers;

/// <summary>Sends a whole source file a lexer reads as its code view when the request can retrieve it, and otherwise drops its blank lines.</summary>
/// <param name="lexers">The lexers whose files get a code view; none keeps every file on the blank-line path.</param>
public sealed class SourceCodeCompressionHandler(IEnumerable<ICodeLexer> lexers) : ICompressionHandler
{
    /// <summary>The path this handler reports when it drops blank lines.</summary>
    public const string HandlerPath = "sourcecode.blank-lines";

    /// <summary>The path this handler reports when it sends a code view.</summary>
    public const string CodeViewPath = "sourcecode.code-view";

    private readonly ICodeLexer[] _lexers = lexers.ToArray();

    /// <inheritdoc />
    public bool CanHandle(ContentType type) => type == ContentType.SourceCode;

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(View(part) ?? new HandledContent(WithoutBlankLines(part.Text), HandlerPath));
    }

    private HandledContent? View(ContentPart part)
    {
        if (part.Ccr == CcrMode.Off || _lexers.FirstOrDefault(lexer => lexer.SupportsPath(part.FilePath)) is not { } lexer)
        {
            return null;
        }

        var id = CcrView.IdOf(part.Text);
        var view = new CodeReducer(lexer).Reduce(part.Text, part.FilePath, new CodeReductionOptions(), CcrView.Hint(id));
        return view.Changed ? new HandledContent(view.Text, CodeViewPath, id) : null;
    }

    private static string WithoutBlankLines(string text)
    {
        var kept = new List<string>();
        foreach (var segment in LiteralBlocks.SourceCodeLiterals(text))
        {
            var line = segment.Text.TrimEnd('\n');

            if (!segment.IsLiteral && line.Trim().Length == 0)
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join("\n", kept);
    }
}
