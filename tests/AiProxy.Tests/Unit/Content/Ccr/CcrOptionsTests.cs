using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>The defaults are the owner's ADR 0005 rulings, pinned as literals so a changed number fails.</summary>
public sealed class CcrOptionsTests
{
    [Fact]
    public void Default_preview_fires_at_3000_tokens_and_holds_at_most_1500()
    {
        Assert.Equal(3_000, CcrOptions.Default.MinimumTokens);
        Assert.Equal(1_500, CcrOptions.Default.BudgetTokens);
    }

    [Fact]
    public void Default_lines_and_pattern_results_are_capped_at_3000_tokens()
    {
        Assert.Equal(3_000, CcrOptions.Default.RetrievalTokens);
    }
}