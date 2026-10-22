using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>
/// The reducers that work on text rather than on a document format: the configuration file,
/// the Markdown document, the source file, the prose, the directory listing and the fallback.
/// </summary>
public class TextCompressionHandlerTests
{
    private static ContentPart Part(ContentType type, string text) => new("request", 1, type, text);

    [Fact]
    public async Task A_comment_line_in_a_configuration_file_goes_away()
    {
        var handler = new TomlCompressionHandler();

        var result = await handler.HandleAsync(Part(ContentType.Toml, "# a comment\nkey = \"value\""), CancellationToken.None);

        Assert.Equal("key = \"value\"", result.Text);
    }

    [Fact]
    public async Task A_hash_inside_a_value_stays()
    {
        var handler = new TomlCompressionHandler();

        var result = await handler.HandleAsync(Part(ContentType.Toml, "colour = \"#ff0000\""), CancellationToken.None);

        Assert.Contains("#ff0000", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_html_comment_in_a_markdown_document_goes_away()
    {
        var handler = new MarkdownCompressionHandler();

        var result = await handler.HandleAsync(Part(ContentType.Markdown, "<!-- hidden -->\n# Title"), CancellationToken.None);

        Assert.DoesNotContain("hidden", result.Text, StringComparison.Ordinal);
        Assert.Contains("# Title", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_of_blank_lines_in_a_markdown_document_collapses()
    {
        var handler = new MarkdownCompressionHandler();

        var result = await handler.HandleAsync(Part(ContentType.Markdown, "one\n\n\n\ntwo"), CancellationToken.None);

        Assert.Equal("one\n\ntwo", result.Text);
    }

    [Fact]
    public async Task A_blank_line_in_a_source_file_goes_away()
    {
        var handler = new SourceCodeCompressionHandler();

        var result = await handler.HandleAsync(Part(ContentType.SourceCode, "var a = 1;\n\nvar b = 2;"), CancellationToken.None);

        Assert.Equal("var a = 1;\nvar b = 2;", result.Text);
    }

    [Fact]
    public async Task A_code_line_stays_whole()
    {
        var handler = new SourceCodeCompressionHandler();

        var result = await handler.HandleAsync(Part(ContentType.SourceCode, "    return null;"), CancellationToken.None);

        Assert.Equal("    return null;", result.Text);
    }

    [Fact]
    public async Task A_repeated_line_in_prose_is_dropped_after_the_first()
    {
        var handler = new ProseCompressionHandler();

        var result = await handler.HandleAsync(Part(ContentType.Prose, "the same line\nthe same line\nanother line"), CancellationToken.None);

        Assert.Equal("the same line\nanother line", result.Text);
    }

    [Fact]
    public async Task A_directory_listing_comes_back_unchanged()
    {
        // The measurement says nothing compresses a listing without losing file names.
        var handler = new DirectoryListingCompressionHandler();
        var listing = "ProviderDetector.cs\nUpstreamForwarder.cs\nDialect.cs";

        var result = await handler.HandleAsync(Part(ContentType.DirectoryListing, listing), CancellationToken.None);

        Assert.Equal(listing, result.Text);
        Assert.Equal("listing.pass-through", result.Path);
    }

    [Fact]
    public async Task The_fallback_changes_nothing()
    {
        var handler = new PassThroughCompressionHandler();

        var result = await handler.HandleAsync(Part(ContentType.Unknown, "anything at all"), CancellationToken.None);

        Assert.Equal("anything at all", result.Text);
        Assert.Equal("unknown.pass-through", result.Path);
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
}
