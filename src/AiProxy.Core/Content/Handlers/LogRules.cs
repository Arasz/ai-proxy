namespace AiProxy.Core.Content.Handlers;

/// <summary>The signal rule the measured log reducer was built from, and its anchors.</summary>
public static class LogRules
{
    /// <summary>The pattern a line carries a signal with.</summary>
    public const string SignalPattern = @"fail|error|timeout|exhaust|refus|denied|fatal|panic|status=[45]\d\d|warn";

    /// <summary>The number of lines kept at each end, whatever they say.</summary>
    public const int Anchors = 2;

    /// <summary>Keeps the lines that carry a signal, plus two anchors at each end. The shell transcript reducer uses it.</summary>
    /// <param name="text">The text to reduce.</param>
    /// <param name="carriesSignal">Whether a line carries a signal.</param>
    public static IReadOnlyList<string> Keep(string text, Func<string, bool> carriesSignal)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(carriesSignal);

        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);

        for (var index = 0; index < lines.Length; index++)
        {
            var anchored = index < Anchors || index >= lines.Length - Anchors;

            if (anchored || carriesSignal(lines[index]))
            {
                kept.Add(lines[index]);
            }
        }

        return kept;
    }
}
