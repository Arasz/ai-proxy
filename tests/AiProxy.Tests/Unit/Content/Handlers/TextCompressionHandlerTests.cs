using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>The reducers that work on text rather than on a document format.</summary>
public class TextCompressionHandlerTests
{
    private static readonly TomlCompressionHandler Toml = new();
    private static readonly MarkdownCompressionHandler Markdown = new();
    private static readonly SourceCodeCompressionHandler SourceCode = new();
    private static readonly ProseCompressionHandler Prose = new();
    private static readonly DirectoryListingCompressionHandler Listing = new();
    private static readonly PassThroughCompressionHandler PassThrough = new();

    [Fact]
    public async Task A_comment_line_in_a_configuration_file_goes_away()
    {
        var result = await Toml.HandleAsync(Part(ContentType.Toml, "# a comment\nkey = \"value\""), CancellationToken.None);

        Assert.Equal("key = \"value\"", result.Text);
    }

    [Fact]
    public async Task A_hash_inside_a_value_stays()
    {
        var result = await Toml.HandleAsync(Part(ContentType.Toml, "colour = \"#ff0000\""), CancellationToken.None);

        Assert.Contains("#ff0000", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_html_comment_in_a_markdown_document_goes_away()
    {
        var result = await Markdown.HandleAsync(Part(ContentType.Markdown, "<!-- hidden -->\n# Title"), CancellationToken.None);

        Assert.DoesNotContain("hidden", result.Text, StringComparison.Ordinal);
        Assert.Contains("# Title", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_of_blank_lines_in_a_markdown_document_collapses()
    {
        var result = await Markdown.HandleAsync(Part(ContentType.Markdown, "one\n\n\n\ntwo"), CancellationToken.None);

        Assert.Equal("one\n\ntwo", result.Text);
    }

    [Fact]
    public async Task A_blank_line_in_a_source_file_goes_away()
    {
        var result = await SourceCode.HandleAsync(Part(ContentType.SourceCode, "var a = 1;\n\nvar b = 2;"), CancellationToken.None);

        Assert.Equal("var a = 1;\nvar b = 2;", result.Text);
    }

    [Fact]
    public async Task A_code_line_stays_whole()
    {
        var result = await SourceCode.HandleAsync(Part(ContentType.SourceCode, "    return null;"), CancellationToken.None);

        Assert.Equal("    return null;", result.Text);
    }

    [Fact]
    public async Task A_repeated_line_in_prose_is_dropped_after_the_first()
    {
        var result = await Prose.HandleAsync(Part(ContentType.Prose, "the same line\nthe same line\nanother line"), CancellationToken.None);

        Assert.Equal("the same line\nanother line", result.Text);
    }

    [Fact]
    public async Task A_directory_listing_comes_back_unchanged()
    {
        // The measurement says nothing compresses a listing without losing file names.
        var listing = "ProviderDetector.cs\nUpstreamForwarder.cs\nDialect.cs";

        var result = await Listing.HandleAsync(Part(ContentType.DirectoryListing, listing), CancellationToken.None);

        Assert.Equal(listing, result.Text);
        Assert.Equal("listing.pass-through", result.Path);
    }

    [Fact]
    public async Task The_fallback_changes_nothing()
    {
        var result = await PassThrough.HandleAsync(Part(ContentType.Unknown, "anything at all"), CancellationToken.None);

        Assert.Equal("anything at all", result.Text);
        Assert.Equal("unknown.pass-through", result.Path);
    }

    [Fact]
    public async Task An_html_comment_inside_a_code_fence_stays()
    {
        // The fence holds code, and a comment in code is code. Reducing inside the
        // fence would change what the agent is shown.
        var markdown = "before\n\n```html\n<!-- a comment in the code -->\n```\n\nafter";

        var result = await Markdown.HandleAsync(Part(ContentType.Markdown, markdown), CancellationToken.None);

        Assert.Contains("<!-- a comment in the code -->", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comment_line_inside_a_multi_line_string_stays()
    {
        // A hash at the start of a line is a comment in a configuration file and part of
        // the value in a multi-line string.
        var toml = "text = \"\"\"\n# not a comment\n\"\"\"\n";

        var result = await Toml.HandleAsync(Part(ContentType.Toml, toml), CancellationToken.None);

        Assert.Contains("# not a comment", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_line_inside_a_raw_string_stays()
    {
        // A blank line inside a raw string is part of the value, and dropping it changes
        // what the file says.
        var code = "var text = \"\"\"\nline one\n\nline two\n\"\"\";";

        var result = await SourceCode.HandleAsync(Part(ContentType.SourceCode, code), CancellationToken.None);

        Assert.Contains("\n\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_reducer_takes_its_own_path_and_not_the_fallback()
    {
        // A reducer that falls back to the line rule is not a reducer that failed loudly.
        // The XML one did exactly that: reading an attribute with a prefix threw on a
        // document that did not declare the prefix, and the fallback swallowed the throw,
        // so the XML reducer deduplicated instead of compacting, for every ordinary
        // document. The path says which path ran, so the path is what a test pins.
        var cases = new (ContentType Kind, ICompressionHandler Handler, string Text, string Path)[]
        {
            (ContentType.Json, new JsonCompressionHandler(), "{ \"a\": 1 }", "json.compact"),
            (ContentType.Xml, new XmlCompressionHandler(), "<root>\n  <a>1</a>\n</root>", "xml.compact"),
            (ContentType.Toml, new TomlCompressionHandler(), "key = \"value\"", "toml.strip-comments"),
            (ContentType.Markdown, new MarkdownCompressionHandler(), "# Title", "markdown.strip-comments"),
            (ContentType.SourceCode, new SourceCodeCompressionHandler(), "var a = 1;", "sourcecode.blank-lines"),
            (ContentType.Prose, new ProseCompressionHandler(), "a line", "prose.dedupe"),
            (ContentType.Log, new LogCompressionHandler(), "2026-03-12 10:00:00 INFO a", "log.signal-filter"),
            (ContentType.ShellTranscript, new ShellTranscriptCompressionHandler(), "$ echo hi", "shell.log-rules"),
            (ContentType.DirectoryListing, new DirectoryListingCompressionHandler(), "a.cs", "listing.pass-through"),
        };

        foreach (var (kind, handler, text, path) in cases)
        {
            var result = await handler.HandleAsync(new ContentPart("request", 1, kind, text), CancellationToken.None);

            Assert.Equal(path, result.Path);
        }
    }

    [Fact]
    public async Task Every_text_reducer_keeps_an_ordered_subsequence_of_its_input()
    {
        var cases = new (ICompressionHandler Handler, ContentType Kind, string Text)[]
        {
            (new TomlCompressionHandler(), ContentType.Toml, "# a comment\nkey = \"value\"\n\n# another\nother = 1"),
            (new MarkdownCompressionHandler(), ContentType.Markdown, "<!-- hidden -->\n# Title\n\n\n\nbody"),
            (new SourceCodeCompressionHandler(), ContentType.SourceCode, "var a = 1;\n\nvar b = 2;\n\n\nreturn a;"),
            (new ProseCompressionHandler(), ContentType.Prose, "the same line\nthe same line\nanother line"),
            (new DirectoryListingCompressionHandler(), ContentType.DirectoryListing, "Dialect.cs\nUpstreamForwarder.cs"),
            (new PassThroughCompressionHandler(), ContentType.Unknown, "anything at all"),
        };

        foreach (var (handler, kind, text) in cases)
        {
            var result = await handler.HandleAsync(Part(kind, text), CancellationToken.None);

            OrderedSubsequence.AssertHolds(result.Text, text);
        }
    }

    [Fact]
    public async Task Each_handler_claims_only_its_own_kind()
    {
        Assert.True(new TomlCompressionHandler().CanHandle(ContentType.Toml));
        Assert.True(new MarkdownCompressionHandler().CanHandle(ContentType.Markdown));
        Assert.True(new SourceCodeCompressionHandler().CanHandle(ContentType.SourceCode));
        Assert.True(new ProseCompressionHandler().CanHandle(ContentType.Prose));
        Assert.True(new DirectoryListingCompressionHandler().CanHandle(ContentType.DirectoryListing));
        Assert.True(new PassThroughCompressionHandler().CanHandle(ContentType.Unknown));
        Assert.False(new TomlCompressionHandler().CanHandle(ContentType.Prose));
    }

    private static ContentPart Part(ContentType type, string text) => new("request", 1, type, text);
}
