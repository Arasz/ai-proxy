using System.Text.RegularExpressions;

namespace AiProxy.Core.Content.Handlers;

/// <summary>Keeps the lines of a log that carry a signal, and drops the rest.</summary>
public sealed class LogCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "log.signal-filter";

    private const int HeadAnchors = 2;
    private const int TailAnchors = 2;

    private static readonly Regex SignalPattern = new(
        @"fail|error|timeout|exhaust|refus|denied|fatal|panic|status=[45]\d\d|warn",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LevelPattern = new(
        @"^\s*(\d{4}-\d{2}-\d{2}[T ]\S+)\s+(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public bool CanHandle(ContentType type) => type == ContentType.Log;

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        var lines = part.Text.Split('\n');
        var kept = new List<string>(lines.Length);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var anchored = index < HeadAnchors || index >= lines.Length - TailAnchors;

            if (!anchored && !CarriesSignal(line))
            {
                continue;
            }

            kept.Add(line);
        }

        var reduced = Reduction.DeduplicateLines(string.Join("\n", kept));
        var overlaid = MustKeepOverlay.Apply(reduced, part.Text);

        return ValueTask.FromResult(new HandledContent(overlaid.Text, HandlerPath));
    }

    private static bool CarriesSignal(string line)
    {
        if (SignalPattern.IsMatch(line))
        {
            return true;
        }

        var match = LevelPattern.Match(line);
        if (!match.Success)
        {
            return false;
        }

        return match.Groups[2].Value is "ERROR" or "FATAL" or "WARN";
    }
}
