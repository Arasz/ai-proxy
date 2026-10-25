using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>
/// The tool result preview, stated as behaviour: a deterministic, budget-bound subset of the
/// original's lines that names every line it hides.
/// </summary>
public sealed partial class CcrPreviewTests
{
    private static readonly CcrOptions Options = CcrOptions.Default;

    private static string Log(int lines, Func<int, string>? line = null)
    {
        line ??= index => $"step {index}: compiling module m{index} in the usual way";
        return string.Join("\n", Enumerable.Range(1, lines).Select(line));
    }

    private static CcrPreview Build(string original) =>
        CcrPreview.Build(original, Options) ?? throw new InvalidOperationException("expected a preview");

    [Fact]
    public void The_same_original_gives_the_same_bytes_every_time()
    {
        var original = Log(400);

        Assert.Equal(Build(original).Text, Build(original).Text);
    }

    [Fact]
    public void The_preview_does_not_depend_on_the_current_culture()
    {
        var original = Log(4000);
        var invariant = Build(original).Text;

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal(invariant, Build(original).Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_id_is_the_first_sixteen_hex_characters_of_the_sha256_of_the_text()
    {
        Assert.Equal("ba7816bf8f01cfea", CcrPreview.IdOf("abc"));
    }

    [Fact]
    public void The_marker_carries_the_id_and_the_line_counts()
    {
        var original = Log(400);

        var preview = Build(original);

        var header = preview.Text.Split('\n')[0];
        Assert.Equal(
            $"[proxy preview {CcrPreview.IdOf(original)}: {preview.ShownLines} of 400 lines shown. Call proxy_retrieve with this id for a line range, a pattern, or the whole text.]",
            header);
        Assert.Equal(CcrPreview.IdOf(original), preview.Id);
    }

    [Fact]
    public void Originals_that_differ_in_their_last_byte_get_different_ids()
    {
        var original = Log(400);

        Assert.NotEqual(CcrPreview.IdOf(original + "a"), CcrPreview.IdOf(original + "b"));
    }

    [Fact]
    public void A_region_one_character_under_the_minimum_is_not_previewed()
    {
        // 3,000 estimated tokens at 2.89 characters each is 8,670 characters.
        var original = new string('x', 59) + string.Concat(Enumerable.Repeat("\n" + new string('y', 99), 86));

        Assert.Equal(8_659, original.Length);
        Assert.Null(CcrPreview.Build(original + new string('z', 10), Options));
    }

    [Fact]
    public void A_region_at_the_minimum_is_previewed()
    {
        var original = new string('x', 59) + string.Concat(Enumerable.Repeat("\n" + new string('y', 99), 86)) + new string('z', 11);

        Assert.Equal(8_670, original.Length);
        Assert.NotNull(CcrPreview.Build(original, Options));
    }

    [Fact]
    public void A_region_that_already_holds_a_preview_marker_is_not_previewed_again()
    {
        var original = "[proxy preview 0123456789abcdef: 1 of 2 lines shown.]\n" + Log(400);

        Assert.Null(CcrPreview.Build(original, Options));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("signals")]
    [InlineData("sparse-signals")]
    [InlineData("wide")]
    [InlineData("identifiers")]
    [InlineData("one-line")]
    public void The_preview_stays_inside_the_budget(string shape)
    {
        var original = Shape(shape);

        var preview = Build(original);

        Assert.True(
            preview.Text.Length <= (int)(Options.BudgetTokens * Options.CharactersPerToken),
            $"{preview.Text.Length} characters is over the budget");
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("signals")]
    [InlineData("sparse-signals")]
    [InlineData("wide")]
    [InlineData("identifiers")]
    [InlineData("crlf")]
    public void Shown_lines_are_verbatim_and_with_the_named_ranges_cover_every_line_once(string shape)
    {
        var original = Shape(shape);
        var lines = original.Split('\n');

        var preview = Build(original);

        var next = 1;
        foreach (var line in Body(preview.Text))
        {
            var gap = GapPattern().Match(line);
            if (gap.Success)
            {
                Assert.Equal(next, int.Parse(gap.Groups[1].Value, CultureInfo.InvariantCulture));
                next = int.Parse(gap.Groups[2].Value, CultureInfo.InvariantCulture) + 1;
                continue;
            }

            Assert.Equal(lines[next - 1], line);
            next++;
        }

        Assert.Equal(lines.Length + 1, next);
    }

    [Fact]
    public void An_error_line_in_the_middle_of_a_build_log_is_kept_at_its_line_number()
    {
        var original = Log(400, index => index == 200
            ? "src/Parser.cs(41,7): error CS1002: ; expected"
            : $"step {index}: compiling module m{index} in the usual way");

        var preview = Build(original);

        Assert.Equal(200, LineNumberOf(preview.Text, "src/Parser.cs(41,7): error CS1002: ; expected"));
    }

    [Fact]
    public void A_flood_of_signal_lines_still_leaves_room_for_the_head_and_the_tail()
    {
        var original = Log(600, index => index is > 5 and < 595
            ? $"test {index} failed: expected 1 but got 2"
            : $"step {index}: setting up the run in the usual way");

        var preview = Build(original);

        Assert.Equal(1, LineNumberOf(preview.Text, "step 1: setting up the run in the usual way"));
        Assert.Equal(600, LineNumberOf(preview.Text, "step 600: setting up the run in the usual way"));
    }

    [Fact]
    public void The_head_gets_two_parts_of_the_space_and_the_tail_one()
    {
        var original = Log(1000);

        var body = Body(Build(original).Text).ToList();
        var firstGap = body.FindIndex(line => GapPattern().IsMatch(line));
        var head = firstGap;
        var tail = body.Count - firstGap - 1;

        Assert.InRange(head, (int)(tail * 1.8), (int)(tail * 2.2));
    }

    [Fact]
    public void An_identifier_on_a_hidden_line_is_listed_after_the_preview()
    {
        var original = Log(400, index => index == 200
            ? "step: loading ConfigurationWatcherFactory from the cache"
            : "step: compiling a module in the usual way");

        var preview = Build(original);

        var overlay = preview.Text[preview.Text.IndexOf(MustKeepOverlay.BlockLabel, StringComparison.Ordinal)..];
        Assert.Contains("ConfigurationWatcherFactory", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_line_longer_than_the_budget_is_cut_and_the_cut_is_named()
    {
        var original = "{\"items\":[" + string.Join(",", Enumerable.Range(1, 2000).Select(index => $"{{\"id\":{index}}}")) + "]}";

        var preview = Build(original);

        var body = Body(preview.Text).ToList();
        Assert.StartsWith(body[0], original, StringComparison.Ordinal);
        Assert.Matches(@"^\[line 1 cut at character \d+ of " + original.Length + @"\]$", body[1]);
        Assert.Equal(1, preview.ShownLines);
    }

    [Fact]
    public void A_single_line_cut_preserves_a_unicode_character_crossing_the_boundary()
    {
        var boundary = Body(Build(new string('q', 20_000)).Text).First().Length;
        var original = new string('q', boundary - 1) + "😀" + new string('q', 20_000 - boundary - 1);

        var preview = Build(original);

        Assert.Equal(new string('q', boundary - 1), Body(preview.Text).First());
        Assert.True(new UTF8Encoding(false, true).GetByteCount(preview.Text) > 0);
    }

    private static string Shape(string shape) => shape switch
    {
        "plain" => Log(400),
        "signals" => Log(800, index => index % 3 == 0 ? $"worker {index} failed with exit code 2" : $"step {index}: fine"),
        "sparse-signals" => Log(2000, index => index % 20 == 0 ? $"worker {index} failed with exit code 2" : $"step {index}: fine"),
        "wide" => Log(300, index => $"{index}: " + new string((char)('a' + (index % 26)), 40 + (index * 7 % 500))),
        "identifiers" => Log(800, index => $"call SomeServiceName{index}.HandleRequest{index} /usr/lib/x{index}.so 0x{index:x4}"),
        "crlf" => Log(400, index => $"step {index}: compiling module m{index}   \r"),
        "one-line" => new string('q', 20_000),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private static IEnumerable<string> Body(string preview)
    {
        var overlay = preview.IndexOf(MustKeepOverlay.BlockLabel, StringComparison.Ordinal);
        var text = overlay < 0 ? preview : preview[..overlay];
        return text.Split('\n').Skip(1);
    }

    private static int LineNumberOf(string preview, string line)
    {
        var next = 1;
        foreach (var shown in Body(preview))
        {
            var gap = GapPattern().Match(shown);
            if (gap.Success)
            {
                next = int.Parse(gap.Groups[2].Value, CultureInfo.InvariantCulture) + 1;
                continue;
            }

            if (shown == line)
            {
                return next;
            }

            next++;
        }

        return -1;
    }

    [GeneratedRegex(@"^\[lines (\d+)-(\d+) not shown\]$")]
    private static partial Regex GapPattern();
}
