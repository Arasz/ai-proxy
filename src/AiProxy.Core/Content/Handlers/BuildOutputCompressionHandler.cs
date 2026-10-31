namespace AiProxy.Core.Content.Handlers;

/// <summary>
///     Reduces MSBuild output: keeps every diagnostic whole, drops exact repeats and shortens the long tool
///     invocations; output it cannot shorten goes through the log reducer.
/// </summary>
public sealed class BuildOutputCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "build.msbuild";

    /// <summary>How many characters of a shortened line survive.</summary>
    public const int HeadLength = 200;

    /// <summary>The length above which a line that is not a diagnostic is shortened.</summary>
    public const int LongLine = 500;

    private static readonly LogCompressionHandler Fallback = new();

    /// <inheritdoc />
    public bool CanHandle(ContentType type)
    {
        return type == ContentType.BuildOutput;
    }

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        var lines = part.Text.Split('\n').Select(static line => MsBuildLines.IsDiagnostic(line) || line.Length <= LongLine ? line : Shorten(line)).ToArray();
        var reduced = string.Join("\n", DropRepeats(lines));
        if (reduced.Length == part.Text.Length)
        {
            return Fallback.HandleAsync(part, cancellationToken);
        }

        // The overlay guards the repeat drop only: a shortened tail is dropped on purpose, named by its marker,
        // and kept by retrieval, and re-attaching its paths would undo the shortening.
        var overlaid = MustKeepOverlay.Apply(reduced, string.Join("\n", lines));

        return ValueTask.FromResult(new HandledContent(overlaid.Text, HandlerPath));
    }

    private static List<string> DropRepeats(string[] lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0 || seen.Add(line))
            {
                kept.Add(line);
            }
        }

        return kept;
    }

    private static string Shorten(string line)
    {
        return $"{line[..HeadLength]} [{line.Length - HeadLength} characters dropped]";
    }
}
