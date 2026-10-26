using System.Text.RegularExpressions;

namespace AiProxy.Core.Content;

/// <summary>What the overlay did to a reduced text.</summary>
/// <param name="Text">The reduced text with the overlay block appended, or the reduced text itself.</param>
/// <param name="RetainedTokens">How many tokens the block holds.</param>
/// <param name="CeilingReached">Whether tokens were dropped to stay inside the ceiling.</param>
public sealed record OverlayResult(string Text, int RetainedTokens, bool CeilingReached);

/// <summary>Re-attaches the facts a reduction dropped, without spending the savings.</summary>
/// <remarks>
///     The rules come from the measured reducer: identifiers, numbers, paths and directive words
///     are the tokens a reduction may never lose, and the overlay puts them back as a trailing
///     block. The record's open question is the decision class, a failure verdict, an exit status
///     or a state transition, which the measured pattern catches only when it is capitalised, so
///     five more rules cover it, and those tokens are the first ones the overlay keeps.
/// </remarks>
public static partial class MustKeepOverlay
{
    /// <summary>The most the overlay may add, as a share of the reduced text.</summary>
    public const double MaximumOverhead = 0.05;

    /// <summary>The least the overlay may add, because a small payload cannot pay for a proportional overhead.</summary>
    public const int MinimumOverhead = 64;

    /// <summary>The label the block carries, so a reader knows it is an addition.</summary>
    public const string BlockLabel = "\n[retained identifiers] ";

    private static readonly Regex IdentifierPattern = CreateIdentifierPattern();

    private static readonly Regex[] DecisionPatterns =
    [
        CreateFailureDecisionPattern(),
        CreateExitCodeDecisionPattern(),
        CreateStateTransitionDecisionPattern(),
        CreateCapitalizedPathDecisionPattern(),
        CreateMustKeepDecisionPattern()
    ];

    /// <summary>Re-attaches the tokens the reduction dropped, as a labelled trailing block.</summary>
    /// <param name="reducedText">The text the reduction produced.</param>
    /// <param name="originalText">The text the reduction started from.</param>
    public static OverlayResult Apply(string reducedText, string originalText)
    {
        var tokens = Tokens(originalText);
        if (tokens.Decisions.Count == 0 && tokens.Others.Count == 0)
        {
            return new OverlayResult(reducedText, 0, false);
        }

        var kept = TokenSet(reducedText);
        var block = new List<string>(tokens.Decisions.Where(token => !kept.Contains(token)));
        var wanted = tokens.Others.Where(token => !kept.Contains(token)).ToList();

        var ceiling = Math.Max(MinimumOverhead, (int)(MaximumOverhead * reducedText.Length));
        var length = BlockLabel.Length + string.Join(" ", block).Length;
        var ceilingReached = false;

        foreach (var token in wanted)
        {
            var added = token.Length + (block.Count > 0 ? 1 : 0);
            if (length + added > ceiling)
            {
                ceilingReached = true;
                break;
            }

            block.Add(token);
            length += added;
        }

        if (block.Count == 0)
        {
            return new OverlayResult(reducedText, 0, ceilingReached);
        }

        return new OverlayResult(reducedText + BlockLabel + string.Join(" ", block), block.Count, ceilingReached);
    }

    /// <summary>Whether a line carries a failure verdict, an exit status or a state transition.</summary>
    public static bool CarriesSignal(string line)
    {
        return DecisionPatterns[0].IsMatch(line) || DecisionPatterns[1].IsMatch(line) || DecisionPatterns[2].IsMatch(line);
    }

    /// <summary>The tokens a reduction may never lose, decision tokens first, each once, in text order.</summary>
    public static IReadOnlyList<string> Identifiers(string text)
    {
        var tokens = Tokens(text);
        return [.. tokens.Decisions, .. tokens.Others];
    }

    private static OverlayTokens Tokens(string text)
    {
        var decisions = new List<string>();
        var others = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in DecisionPatterns)
        foreach (Match match in pattern.Matches(text))
        {
            if (seen.Add(match.Value))
            {
                decisions.Add(match.Value);
            }
        }

        foreach (Match match in IdentifierPattern.Matches(text))
        {
            if (seen.Add(match.Value))
            {
                others.Add(match.Value);
            }
        }

        return new OverlayTokens(decisions, others);
    }

    private static HashSet<string> TokenSet(string text)
    {
        var tokens = Tokens(text);
        var set = new HashSet<string>(tokens.Decisions, StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens.Others)
        {
            set.Add(token);
        }

        return set;
    }

    [GeneratedRegex(
        @"\b0x[0-9A-Fa-f]+\b|(?<![\w.])\d+(?:\.\d+)?(?![\w.])|[A-Z_]{2,}|[a-z_][a-z0-9_]*\.[a-z0-9_]+|/[a-z0-9/._-]{2,}|\.[a-z]{2,4}\b|--?[a-z][\w-]*|\b[A-Z][a-z]+[A-Z]\w*|(?i:\b(?:not|never|none|cannot|can't|don't|doesn't|didn't|won't|shouldn't|mustn't|isn't|aren't|avoid|refuse|prohibited|forbidden|disallow|unless|except|without|must|should|shall|required|always|only|mandatory|and|or|nor|xor)\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateIdentifierPattern();

    [GeneratedRegex(@"(?i)\b(?:failed|failure|fatal|critical|error|exception|panic)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CreateFailureDecisionPattern();

    [GeneratedRegex(@"(?i)\bexit(?:\s+code)?\s+\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CreateExitCodeDecisionPattern();

    [GeneratedRegex(@"(?i)\b(?:retried|requeued|quarantined|aborted|rolled\s+back|timed\s+out|skipped|reverted)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CreateStateTransitionDecisionPattern();

    [GeneratedRegex(@"/[A-Za-z0-9_./\-]*[A-Z][A-Za-z0-9_./\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex CreateCapitalizedPathDecisionPattern();

    [GeneratedRegex(@"(?i)\b(?:must-keep|never\s+drop|do\s+not\s+drop|keep\s+this)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CreateMustKeepDecisionPattern();

    /// <summary>The tokens of a text: decision tokens first, then the other identifiers, each once.</summary>
    private sealed record OverlayTokens(List<string> Decisions, List<string> Others);
}