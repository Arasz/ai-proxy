using AiProxy.Core.Gain;

namespace AiProxy.Core.Content.Ccr;

/// <summary>The numbers recoverable compression runs with; every default is an owner-approved ADR 0005 value.</summary>
/// <param name="CharactersPerToken">Characters per estimated token.</param>
/// <param name="MinimumTokens">The smallest whole region, in estimated tokens, that gets a preview.</param>
/// <param name="BudgetTokens">The most a preview may hold, in estimated tokens, markers and overlay included.</param>
/// <param name="BudgetShare">The share of the region's original tokens a preview may hold; the budget is the smaller of this and <paramref name="BudgetTokens" />.</param>
/// <param name="MinimumSavingTokens">The least a preview must save over the reduced text, in estimated tokens; a smaller saving sends the reduced text.</param>
/// <param name="SignalShare">The share of the line budget signal lines may take first.</param>
/// <param name="HeadShare">The share of what is left after signal lines that goes to the head; the tail gets the rest.</param>
/// <param name="OverlayShare">The share of the budget the trailing identifier block may take.</param>
/// <param name="RetrievalTokens">The most a <c>lines</c> or <c>pattern</c> result may hold, in estimated tokens.</param>
/// <param name="MaximumMatches">The most matching lines a <c>pattern</c> result returns.</param>
/// <param name="MatchWindow">The characters shown around a match on a longer line.</param>
/// <param name="MaximumContext">The most context lines a <c>pattern</c> call may ask for.</param>
/// <param name="PatternTimeout">How long one pattern match may run.</param>
/// <param name="SkipUnderTokens">A numbered read or a diff whose reduced text is under this many estimated tokens is not previewed; 0 switches the skip off.</param>
public sealed record CcrOptions(
    decimal CharactersPerToken,
    int MinimumTokens,
    int BudgetTokens,
    decimal BudgetShare,
    int MinimumSavingTokens,
    decimal SignalShare,
    decimal HeadShare,
    decimal OverlayShare,
    int RetrievalTokens,
    int MaximumMatches,
    int MatchWindow,
    int MaximumContext,
    TimeSpan PatternTimeout,
    int SkipUnderTokens)
{
    /// <summary>The ADR 0005 and 0015 values.</summary>
    public static CcrOptions Default { get; } = new(
        GainReportBuilder.CharactersPerToken,
        1_000,
        1_500,
        0.40m,
        0,
        0.40m,
        2m / 3m,
        0.05m,
        3_000,
        50,
        240,
        10,
        TimeSpan.FromMilliseconds(250),
        6_000);

    /// <summary>The most a preview of a region of this many original tokens may hold, in estimated tokens.</summary>
    /// <param name="originalTokens">The region's estimated tokens as the client sent it; reducers do not change the budget.</param>
    public int BudgetFor(int originalTokens) => Math.Min(BudgetTokens, (int)(BudgetShare * originalTokens));

    /// <summary>Whether a region is a numbered read or a diff small enough, after its reducers, to go out without a preview.</summary>
    /// <param name="original">The region's text as the client sent it.</param>
    /// <param name="reducedTokens">The region's estimated tokens after its reducers.</param>
    public bool SkipsPreview(string original, int reducedTokens) => reducedTokens < SkipUnderTokens && (PreviewSkip.IsNumberedRead(original) || PreviewSkip.IsDiff(original));

    /// <summary>The estimated tokens of a text of this many characters, rounded down.</summary>
    public int TokensOf(int characters) => (int)(characters / CharactersPerToken);

    /// <summary>The most characters that still fit in this many estimated tokens.</summary>
    public int CharactersOf(int tokens) => (int)(tokens * CharactersPerToken);
}