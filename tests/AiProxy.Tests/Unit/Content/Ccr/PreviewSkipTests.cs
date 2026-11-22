using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>Numbered reads and diffs are recognised by line-anchored shape, and nothing else is.</summary>
public sealed class PreviewSkipTests
{
    private static string Numbered(int start, int count) => string.Join("\n", Enumerable.Range(start, count).Select(number => $"{number,6}\tline {number}"));

    public static TheoryData<string> Reads =>
    [
        Numbered(1, 40),
        Numbered(240, 40),
        Numbered(1, 5),
        $"{Numbered(1, 5)}\n"
    ];

    public static TheoryData<string> Diffs =>
    [
        "diff --cc a.cs\nindex 1,2..3\n@@@ -1,2 -1,2 +1,2 @@@\n-a\n+b",
        "--- a/a.cs\n+++ b/a.cs\n@@@ -1,2 -1,2 +1,2 @@@\n-a\n+b",
        "diff --git a/a.cs b/a.cs\nindex 1..2 100644\n--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,2 @@\n-a\n+b",
        "commit 0123abc\nAuthor: A <a@x>\nDate:   Mon\n\n    message\n\ndiff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-a\n+b",
        "--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-a\n+b"
    ];

    public static TheoryData<string> NotReads =>
    [
        "1024\t./src\n2048\t./docs\n4096\t./tests\n512\t./bin\n8\t./obj",
        "1\talice\t30\n2\tbob\t41\n7\tcarol\t52\n9\tdave\t60\n11\teve\t70",
        "  1204\tAlice\n   988\tBob\n   402\tCarol\n   100\tDave\n    55\tEve",
        "line one\nline two\nline three",
        "     1 \tnot a tab after digits? no: digits then space",
        "",
        $"{Numbered(1, 3)}\nplain line",
        $"{Numbered(1, 4)}\nplain fifth line",
        Numbered(1, 4),
        $"{Numbered(1, 4)}\n",
        "7\tonly one tabbed row"
    ];

    public static TheoryData<string> NotDiffs =>
    [
        "# Title\n\n---\n\ntext\n\n---\n+++ not a diff\n",
        "{\"patch\":\"@@ -1 +1 @@\"}\n{\"file\":\"a.cs\"}",
        "2026-11-06 build failed: see diff --git a/x b/x in the log",
        "---\ntitle: Notes\ntags: [a]\n---\nbody",
        "--- a/a.cs\n+++ b/a.cs\nno hunk header\n",
        $"{string.Join("\n", Enumerable.Repeat("filler", 60))}\ndiff --git a/a.cs b/a.cs"
    ];

    [Theory]
    [MemberData(nameof(Reads))]
    public void A_numbered_read_is_recognised(string text) => Assert.True(PreviewSkip.IsNumberedRead(text));

    [Theory]
    [MemberData(nameof(NotReads))]
    public void Tabbed_rows_that_are_not_consecutive_line_numbers_are_not_a_read(string text) => Assert.False(PreviewSkip.IsNumberedRead(text));

    [Theory]
    [MemberData(nameof(Diffs))]
    public void A_diff_is_recognised(string text) => Assert.True(PreviewSkip.IsDiff(text));

    [Theory]
    [MemberData(nameof(NotDiffs))]
    public void Text_that_only_mentions_diff_markers_is_not_a_diff(string text) => Assert.False(PreviewSkip.IsDiff(text));

    [Fact]
    public void Shortlog_output_is_neither_a_read_nor_a_diff()
    {
        const string shortlog = "    42\tAlice\n    17\tBob\n     9\tCarol\n     3\tDave\n     1\tEve";

        Assert.False(PreviewSkip.IsNumberedRead(shortlog));
        Assert.False(PreviewSkip.IsDiff(shortlog));
    }

    [Fact]
    public void A_diff_header_on_line_50_is_recognised_and_on_line_51_is_not()
    {
        var onFifty = $"{string.Join("\n", Enumerable.Repeat("filler", 49))}\ndiff --git a/a.cs b/a.cs";
        var onFiftyOne = $"{string.Join("\n", Enumerable.Repeat("filler", 50))}\ndiff --git a/a.cs b/a.cs";

        Assert.True(PreviewSkip.IsDiff(onFifty));
        Assert.False(PreviewSkip.IsDiff(onFiftyOne));
    }

    [Fact]
    public void A_read_and_a_diff_with_windows_line_endings_are_recognised()
    {
        Assert.True(PreviewSkip.IsNumberedRead($"{Numbered(1, 40).Replace("\n", "\r\n")}\r\n"));
        Assert.True(PreviewSkip.IsDiff("diff --git a/a.cs b/a.cs\r\n--- a/a.cs\r\n+++ b/a.cs\r\n@@ -1 +1 @@\r\n-a\r\n+b\r\n"));
        Assert.True(PreviewSkip.IsDiff("--- a/a.cs\r\n+++ b/a.cs\r\n@@ -1 +1 @@\r\n-a\r\n+b\r\n"));
    }
}
