using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>The preview budget scales with the reduced text, the floor is checked once on it, and a preview must save tokens.</summary>
public sealed class CcrPreviewBudgetTests
{
    private static readonly CcrOptions Options = CcrOptions.Default;

    private static string Log(int lines)
    {
        return string.Join("\n", Enumerable.Range(1, lines).Select(index => $"step {index}: compiling module m{index} in the usual way"));
    }

    private static CcrPreview? Build(string original, CcrOptions options, int reducedTokens, CcrPreviewBuilder? builder = null)
    {
        return (builder ?? new CcrPreviewBuilder(new CcrPreviewOverlay())).Build(original, options, reducedTokens);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(400)]
    public void The_preview_holds_at_most_the_share_of_the_reduced_tokens_and_the_cap(int lines)
    {
        var original = Log(lines);
        var reduced = Options.TokensOf(original.Length);

        var preview = Build(original, Options, reduced) ?? throw new InvalidOperationException("expected a preview");

        var ceiling = Math.Min(1_500, (int)(0.4m * reduced));
        Assert.True(Options.TokensOf(preview.Text.Length) <= ceiling, $"{Options.TokensOf(preview.Text.Length)} tokens is over {ceiling}");
    }

    [Fact]
    public void The_budget_follows_the_reduced_tokens_not_the_original()
    {
        var original = Log(400);

        var small = Build(original, Options, 3_000) ?? throw new InvalidOperationException("expected a preview");

        Assert.True(Options.TokensOf(small.Text.Length) <= 1_200);
    }

    [Fact]
    public void A_reused_builder_gives_the_bytes_a_fresh_builder_gives()
    {
        var builder = new CcrPreviewBuilder(new CcrPreviewOverlay());
        var big = Log(400);
        var small = Log(120);

        Build(big, Options, Options.TokensOf(big.Length), builder);
        var reused = Build(small, Options, Options.TokensOf(small.Length), builder);

        Assert.NotNull(reused);
        Assert.Equal(Build(small, Options, Options.TokensOf(small.Length)), reused);
        Assert.Equal(reused, Build(small, Options, Options.TokensOf(small.Length), builder));
    }

    [Fact]
    public void A_preview_not_shorter_than_the_reduced_text_is_not_made()
    {
        var original = Log(120);
        var wide = Options with { BudgetShare = 2m };

        Assert.Null(Build(original, wide, 600));
    }

    [Fact]
    public void A_preview_that_saves_less_than_the_minimum_saving_is_not_made()
    {
        var original = Log(400);
        var reduced = Options.TokensOf(original.Length);
        var saving = reduced - Options.TokensOf((Build(original, Options, reduced) ?? throw new InvalidOperationException("expected a preview")).Text.Length);

        Assert.NotNull(Build(original, Options with { MinimumSavingTokens = saving }, reduced));
        Assert.Null(Build(original, Options with { MinimumSavingTokens = saving + 1 }, reduced));
    }

    [Fact]
    public async Task The_floor_applies_to_the_reduced_text_not_the_original()
    {
        var previewer = new CcrPreviewer(new MemoryOriginalStore(), () => new CcrPreviewBuilder(new CcrPreviewOverlay()), Options);
        var original = Log(400);

        var shrunk = await previewer.PreviewAsync(original, Log(50), CcrMode.Stateless, CancellationToken.None);
        var kept = await previewer.PreviewAsync(original, original, CcrMode.Stateless, CancellationToken.None);

        Assert.Null(shrunk);
        Assert.NotNull(kept);
    }

    [Fact]
    public async Task The_floor_is_met_at_the_boundary_of_the_reduced_text()
    {
        var previewer = new CcrPreviewer(new MemoryOriginalStore(), () => new CcrPreviewBuilder(new CcrPreviewOverlay()), Options);
        var original = new string('x', 59) + string.Concat(Enumerable.Repeat($"\n{new string('y', 99)}", 86));

        Assert.Null(await previewer.PreviewAsync(original + new string('z', 10), original + new string('z', 10), CcrMode.Stateless, CancellationToken.None));
        Assert.NotNull(await previewer.PreviewAsync(original + new string('z', 11), original + new string('z', 11), CcrMode.Stateless, CancellationToken.None));
    }
}
