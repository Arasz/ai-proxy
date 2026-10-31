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

    /// <summary>The length above which a tool invocation is shortened.</summary>
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
        var lines = part.Text.Split('\n').Select(static line => IsInvocation(line) ? Shorten(line) : line).ToArray();
        var reduced = string.Join("\n", DropRepeats(lines));
        if (reduced.Length == part.Text.Length)
        {
            return Fallback.HandleAsync(part, cancellationToken);
        }

        return ValueTask.FromResult(new HandledContent(reduced, HandlerPath));
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

    // A compiler command line or response-file dump: long, and mostly paths and switches rather than words.
    private static bool IsInvocation(string line)
    {
        if (line.Length <= LongLine || MsBuildLines.IsDiagnostic(line))
        {
            return false;
        }

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Count(static token => token.StartsWith('-') || token.Contains('/') || token.Contains('\\')) * 2 >= tokens.Length;
    }

    private static string Shorten(string line)
    {
        return $"{line[..HeadLength]} [{line.Length - HeadLength} characters dropped]";
    }
}
