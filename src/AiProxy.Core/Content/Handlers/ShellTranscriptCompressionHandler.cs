namespace AiProxy.Core.Content.Handlers;

/// <summary>Keeps the lines of a shell transcript that carry a signal, plus two anchors at each end.</summary>
public sealed class ShellTranscriptCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "shell.log-rules";

    private static readonly System.Text.RegularExpressions.Regex SignalPattern = new(
        LogRules.SignalPattern,
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public bool CanHandle(ContentType type) => type == ContentType.ShellTranscript;

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        var kept = LogRules.Keep(part.Text, line => SignalPattern.IsMatch(line));
        var reduced = Reduction.DeduplicateLines(string.Join("\n", kept));
        var overlaid = MustKeepOverlay.Apply(reduced, part.Text);

        return ValueTask.FromResult(new HandledContent(overlaid.Text, HandlerPath));
    }
}
