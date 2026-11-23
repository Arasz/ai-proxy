using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>The preview budget scales with the original text, the floor is checked once on the reduced text, and a preview must save tokens.</summary>
public sealed class CcrPreviewBudgetTests
{
    private static readonly CcrOptions Options = CcrOptions.Default;

    private static CcrPreview Made(CcrPreviewResult result) => Assert.IsType<CcrPreview>(result.Value);

    private static string Log(int lines) => string.Join("\n", Enumerable.Range(1, lines).Select(index => $"step {index}: compiling module m{index} in the usual way"));

    private static CcrPreviewResult Build(string original, CcrOptions options, int reducedTokens, CcrPreviewBuilder? builder = null) => (builder ?? new CcrPreviewBuilder(new CcrPreviewOverlay())).Build(original, options, reducedTokens);

    [Theory]
    [InlineData(120)]
    [InlineData(400)]
    public void The_preview_holds_at_most_the_share_of_the_original_tokens_and_the_cap(int lines)
    {
        var original = Log(lines);
        var reduced = Options.TokensOf(original.Length);

        var preview = Made(Build(original, Options, reduced));

        var ceiling = Math.Min(1_500, (int)(0.4m * reduced));
        Assert.True(Options.TokensOf(preview.Text.Length) <= ceiling, $"{Options.TokensOf(preview.Text.Length)} tokens is over {ceiling}");
    }

    [Fact]
    public void The_budget_follows_the_original_tokens_not_the_reduced()
    {
        var original = Log(400);
        var originalTokens = Options.TokensOf(original.Length);

        var afterReducers = Made(Build(original, Options, 3_000));
        var afterNone = Made(Build(original, Options, originalTokens));

        Assert.Equal(afterNone, afterReducers);
    }

    [Fact]
    public void A_preview_that_is_exactly_as_long_as_the_reduced_text_is_not_made_and_one_token_shorter_is()
    {
        var original = Log(400);
        var previewTokens = Options.TokensOf(Made(Build(original, Options, Options.TokensOf(original.Length))).Text.Length);

        Assert.IsType<NoCcrPreview>(Build(original, Options, previewTokens).Value);
        Assert.IsType<CcrPreview>(Build(original, Options, previewTokens + 1).Value);
    }

    [Fact]
    public void A_reused_builder_gives_the_bytes_a_fresh_builder_gives()
    {
        var builder = new CcrPreviewBuilder(new CcrPreviewOverlay());
        var big = Log(400);
        var small = Log(120);

        Build(big, Options, Options.TokensOf(big.Length), builder);
        var reused = Build(small, Options, Options.TokensOf(small.Length), builder);

        var made = Made(reused);
        Assert.Equal(Made(Build(small, Options, Options.TokensOf(small.Length))), made);
        Assert.Equal(made, Made(Build(small, Options, Options.TokensOf(small.Length), builder)));
    }

    [Fact]
    public void A_preview_not_shorter_than_the_reduced_text_is_not_made()
    {
        var original = Log(120);
        var wide = Options with { BudgetShare = 2m };

        Assert.IsType<NoCcrPreview>(Build(original, wide, 600).Value);
    }

    [Fact]
    public void A_preview_that_saves_less_than_the_minimum_saving_is_not_made()
    {
        var original = Log(400);
        var reduced = Options.TokensOf(original.Length);
        var saving = reduced - Options.TokensOf(Made(Build(original, Options, reduced)).Text.Length);

        Assert.IsType<CcrPreview>(Build(original, Options with { MinimumSavingTokens = saving }, reduced).Value);
        Assert.IsType<NoCcrPreview>(Build(original, Options with { MinimumSavingTokens = saving + 1 }, reduced).Value);
    }

    [Fact]
    public async Task The_floor_applies_to_the_reduced_text_not_the_original()
    {
        var previewer = new CcrPreviewer(new MemoryOriginalStore(), () => new CcrPreviewBuilder(new CcrPreviewOverlay()), Options);
        var original = Log(400);

        var shrunk = await previewer.PreviewAsync(original, Log(50), CcrMode.Stateless, CancellationToken.None);
        var kept = await previewer.PreviewAsync(original, original, CcrMode.Stateless, CancellationToken.None);

        Assert.IsType<NoCcrPreview>(shrunk.Value);
        Assert.IsType<CcrPreview>(kept.Value);
    }

    [Fact]
    public async Task The_floor_is_met_at_the_boundary_of_the_reduced_text()
    {
        var previewer = new CcrPreviewer(new MemoryOriginalStore(), () => new CcrPreviewBuilder(new CcrPreviewOverlay()), Options with { MinimumTokens = 3_000 });
        var original = new string('x', 59) + string.Concat(Enumerable.Repeat($"\n{new string('y', 99)}", 86));

        Assert.IsType<NoCcrPreview>((await previewer.PreviewAsync(original + new string('z', 10), original + new string('z', 10), CcrMode.Stateless, CancellationToken.None)).Value);
        Assert.IsType<CcrPreview>((await previewer.PreviewAsync(original + new string('z', 11), original + new string('z', 11), CcrMode.Stateless, CancellationToken.None)).Value);
    }
}
