using System.Text.RegularExpressions;

namespace AiProxy.Core.Content.Handlers;

/// <summary>Keeps the lines of a log that carry a signal whole, and shortens repeats of a line's shape to what changed.</summary>
public sealed class LogCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports.</summary>
    public const string HandlerPath = "log.signal-filter";

    private static readonly Regex SignalPattern = new(
        LogRules.SignalPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LevelPattern = new(
        @"^\s*(\d{4}-\d{2}-\d{2}[T ]\S+)\s+(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public bool CanHandle(ContentType type)
    {
        return type == ContentType.Log;
    }

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        var unique = Reduction.DeduplicateLines(part.Text);
        var reduced = string.Join("\n", LineShapes.Collapse(unique, CarriesSignal));
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