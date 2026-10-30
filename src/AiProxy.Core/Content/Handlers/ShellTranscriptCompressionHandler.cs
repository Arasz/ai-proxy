using System.Text.RegularExpressions;

namespace AiProxy.Core.Content.Handlers;

/// <summary>Keeps the lines of a shell transcript that carry a signal, plus two anchors at each end.</summary>
public sealed class ShellTranscriptCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "shell.log-rules";

    private static readonly Regex SignalPattern = new(
        LogRules.SignalPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public bool CanHandle(ContentType type)
    {
        return type == ContentType.ShellTranscript;
    }

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {

        var kept = LogRules.Keep(part.Text, line => SignalPattern.IsMatch(line));
        var reduced = Reduction.DeduplicateLines(string.Join("\n", kept));
        var overlaid = MustKeepOverlay.Apply(reduced, part.Text);

        return ValueTask.FromResult(new HandledContent(overlaid.Text, HandlerPath));
    }
}