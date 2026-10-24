using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>The transcript reducer, stated as behaviour: the failure survives, the noise does not.</summary>
public sealed class ShellTranscriptCompressionHandlerTests
{
    private static readonly ShellTranscriptCompressionHandler Handler = new();

    private static ContentPart Part(string text) => new("request", 1, ContentType.ShellTranscript, text);

    [Fact]
    public async Task The_line_that_carries_the_failure_survives()
    {
        var text = string.Join("\n",
        [
            "$ ls -la",
            "total 4",
            "drwxr-xr-x 2 user group 4096 .",
            "$ grep -r TODO .",
            "src/a.cs:3: TODO: fix this",
            "src/b.cs:9: TODO: and this",
            "src/c.cs:11: TODO: and this",
            "$ echo done",
        ]);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Contains("TODO", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_without_a_signal_outside_the_anchors_is_dropped()
    {
        var text = string.Join("\n",
        [
            "$ ls -la",
            "total 4",
            "nothing here",
            "nothing here",
            "nothing here",
            "nothing here",
            "drwxr-xr-x 2 user group 4096 .",
            "$ echo done",
        ]);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.DoesNotContain("nothing here", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reduction_is_an_ordered_subsequence_of_the_input()
    {
        var text = string.Join("\n",
        [
            "$ ls -la",
            "total 4",
            "nothing here",
            "nothing here",
            "nothing here",
            "nothing here",
            "drwxr-xr-x 2 user group 4096 .",
            "$ echo done",
        ]);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        OrderedSubsequence.AssertHolds(result.Text, text);
    }

    [Fact]
    public async Task The_handler_says_which_path_it_took()
    {
        var result = await Handler.HandleAsync(Part("$ echo hi"), CancellationToken.None);

        Assert.Equal("shell.log-rules", result.Path);
    }

    [Fact]
    public async Task The_handler_claims_the_transcript_kind_and_nothing_else()
    {
        Assert.True(Handler.CanHandle(ContentType.ShellTranscript));
        Assert.False(Handler.CanHandle(ContentType.Log));
    }
}
