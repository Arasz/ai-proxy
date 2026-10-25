namespace AiProxy.Core.Content.Ccr;

/// <summary>The numbers recoverable compression runs with; every default is an owner-approved ADR 0005 value.</summary>
/// <param name="CharactersPerToken">Characters per estimated token.</param>
/// <param name="MinimumTokens">The smallest whole region, in estimated tokens, that gets a preview.</param>
/// <param name="BudgetTokens">The most a preview may hold, in estimated tokens, markers and overlay included.</param>
/// <param name="SignalShare">The share of the line budget signal lines may take first.</param>
/// <param name="HeadShare">The share of what is left after signal lines that goes to the head; the tail gets the rest.</param>
/// <param name="OverlayShare">The share of the budget the trailing identifier block may take.</param>
/// <param name="RetrievalTokens">The most a <c>lines</c> or <c>pattern</c> result may hold, in estimated tokens.</param>
/// <param name="MaximumMatches">The most matching lines a <c>pattern</c> result returns.</param>
/// <param name="MatchWindow">The characters shown around a match on a longer line.</param>
/// <param name="MaximumContext">The most context lines a <c>pattern</c> call may ask for.</param>
/// <param name="PatternTimeout">How long one pattern match may run.</param>
public sealed record CcrOptions(
    decimal CharactersPerToken,
    int MinimumTokens,
    int BudgetTokens,
    decimal SignalShare,
    decimal HeadShare,
    decimal OverlayShare,
    int RetrievalTokens,
    int MaximumMatches,
    int MatchWindow,
    int MaximumContext,
    TimeSpan PatternTimeout)
{
    /// <summary>The owner-approved ADR 0005 values.</summary>
    public static CcrOptions Default { get; } = new(
        CharactersPerToken: 2.89m,
        MinimumTokens: 3_000,
        BudgetTokens: 1_500,
        SignalShare: 0.40m,
        HeadShare: 2m / 3m,
        OverlayShare: 0.05m,
        RetrievalTokens: 3_000,
        MaximumMatches: 50,
        MatchWindow: 240,
        MaximumContext: 10,
        PatternTimeout: TimeSpan.FromMilliseconds(250));

    /// <summary>The estimated tokens of a text of this many characters, rounded down.</summary>
    public int TokensOf(int characters) => (int)(characters / CharactersPerToken);

    /// <summary>The most characters that still fit in this many estimated tokens.</summary>
    public int CharactersOf(int tokens) => (int)(tokens * CharactersPerToken);
}
