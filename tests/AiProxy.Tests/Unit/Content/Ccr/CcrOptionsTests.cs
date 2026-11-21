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

    [Fact]
    public void Default_skip_is_off_until_the_tune_step_sets_a_band()
    {
        Assert.Equal(0, CcrOptions.Default.SkipUnderTokens);
        Assert.False(CcrOptions.Default.SkipsPreview("     1\ta\n     2\tb\n     3\tc\n     4\td\n     5\te", 10_000));
    }

    [Fact]
    public void A_numbered_read_or_a_diff_under_the_band_is_skipped_and_one_at_the_band_is_not()
    {
        var options = CcrOptions.Default with { SkipUnderTokens = 100 };
        const string read = "     1\ta\n     2\tb\n     3\tc\n     4\td\n     5\te";

        Assert.True(options.SkipsPreview(read, 99));
        Assert.False(options.SkipsPreview(read, 100));
        Assert.True(options.SkipsPreview("diff --git a/a b/a\n--- a/a\n+++ b/a\n@@ -1 +1 @@\n-a\n+b", 99));
        Assert.False(options.SkipsPreview("plain text\nmore text", 99));
    }
}
