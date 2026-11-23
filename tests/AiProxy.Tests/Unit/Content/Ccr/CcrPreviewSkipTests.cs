using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>A numbered read or a diff below the skip band goes out as the reducers left it, never as a preview.</summary>
public sealed class CcrPreviewSkipTests
{
    private static readonly CcrOptions Options = CcrOptions.Default;

    private static string Read(int lines) => string.Join("\n", Enumerable.Range(240, lines).Select(number => $"{number,6}\tstep {number}: compiling module m{number} in the usual way"));

    private static string Diff(int hunks) =>
        $"diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n{string.Concat(Enumerable.Range(1, hunks).Select(index => $"@@ -{index},3 +{index},3 @@\n context line number {index} of the change\n-the old line {index} that was removed here\n+the new line {index} that was added here\n"))}";

    private static CcrPreviewer Previewer(CcrOptions options) => new(new MemoryOriginalStore(), () => new CcrPreviewBuilder(new CcrPreviewOverlay()), options);

    [Fact]
    public async Task A_numbered_read_of_eight_thousand_tokens_is_previewed_with_the_default_options()
    {
        var read = Read(400);
        Assert.InRange(Options.TokensOf(read.Length), 7_000, 9_000);

        Assert.IsType<CcrPreview>((await Previewer(Options).PreviewAsync(read, read, CcrMode.Stateless, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task A_numbered_read_under_a_set_band_is_not_previewed()
    {
        var read = Read(300);

        Assert.IsType<NoCcrPreview>((await Previewer(Options with { SkipUnderTokens = 100_000 }).PreviewAsync(read, read, CcrMode.Stateless, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task A_skipped_preview_is_the_shared_none_value()
    {
        var read = Read(300);

        Assert.Same(NoCcrPreview.Instance, (await Previewer(Options with { SkipUnderTokens = 100_000 }).PreviewAsync(read, read, CcrMode.Stateless, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task A_numbered_read_is_previewed_at_and_above_the_band()
    {
        var read = Read(300);
        var tokens = Options.TokensOf(read.Length);

        Assert.IsType<NoCcrPreview>((await Previewer(Options with { SkipUnderTokens = tokens + 1 }).PreviewAsync(read, read, CcrMode.Stateless, CancellationToken.None)).Value);
        Assert.IsType<CcrPreview>((await Previewer(Options with { SkipUnderTokens = tokens }).PreviewAsync(read, read, CcrMode.Stateless, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task The_same_lines_without_numbers_are_previewed()
    {
        var plain = string.Join("\n", Enumerable.Range(240, 300).Select(number => $"step {number}: compiling module m{number} in the usual way"));

        Assert.IsType<CcrPreview>((await Previewer(Options).PreviewAsync(plain, plain, CcrMode.Stateless, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task A_diff_around_four_hundred_tokens_gets_no_scaled_preview()
    {
        var floor = Options with { MinimumTokens = 300, SkipUnderTokens = 100_000 };
        var diff = Diff(9);
        var tokens = floor.TokensOf(diff.Length);
        Assert.InRange(tokens, 350, 450);

        Assert.IsType<NoCcrPreview>((await Previewer(floor).PreviewAsync(diff, diff, CcrMode.Stateless, CancellationToken.None)).Value);
        Assert.IsType<CcrPreview>((await Previewer(floor with { SkipUnderTokens = 0 }).PreviewAsync(diff, diff, CcrMode.Stateless, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task The_shape_is_read_from_the_original_not_the_reduced_text()
    {
        var read = Read(300);
        var reduced = string.Join("\n", Enumerable.Range(1, 300).Select(index => $"view line {index} of a rewritten read, not numbered"));

        Assert.IsType<NoCcrPreview>((await Previewer(Options with { SkipUnderTokens = 100_000 }).PreviewAsync(read, reduced, CcrMode.Stateless, CancellationToken.None)).Value);
    }
}
