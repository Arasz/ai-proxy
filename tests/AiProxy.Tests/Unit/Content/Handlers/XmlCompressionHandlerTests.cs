using System.Xml.Linq;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>The XML reducer, stated as behaviour: the indentation goes and the text stays.</summary>
public sealed class XmlCompressionHandlerTests
{
    private static readonly XmlCompressionHandler Handler = new();

    private static ContentPart Part(string text)
    {
        return new ContentPart("request", 1, ContentType.Xml, text);
    }

    [Fact]
    public async Task The_indentation_goes_away()
    {
        var result = await Handler.HandleAsync(Part("<root>\n  <a>1</a>\n</root>"), CancellationToken.None);

        Assert.Equal("<root><a>1</a></root>", result.Text);
    }

    [Fact]
    public async Task The_text_content_survives()
    {
        var result = await Handler.HandleAsync(Part("<root>\n  <a>keep me</a>\n</root>"), CancellationToken.None);

        Assert.Contains("keep me", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_spaces_in_mixed_content_survive()
    {
        // In mixed content the whitespace is the content: "Hello <b>world</b>" reads
        // differently without the space, and a reducer that drops it has changed the
        // document rather than the formatting of it.
        var original = "<p>Hello <b>world</b> !</p>";

        var result = await Handler.HandleAsync(Part(original), CancellationToken.None);

        Assert.Equal(original, result.Text);
    }

    [Fact]
    public async Task The_whitespace_marked_preserve_survives()
    {
        var original = "<p xml:space=\"preserve\">  spaced  </p>";

        var result = await Handler.HandleAsync(Part(original), CancellationToken.None);

        Assert.Contains("  spaced  ", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_indentation_of_an_element_that_has_only_elements_goes_away()
    {
        var result = await Handler.HandleAsync(Part("<root>\n  <a>1</a>\n  <b>2</b>\n</root>"), CancellationToken.None);

        Assert.Equal("<root><a>1</a><b>2</b></root>", result.Text);
    }

    [Fact]
    public async Task The_values_of_the_document_survive()
    {
        var original = "<root><a>1</a><b>keep me</b></root>";

        var result = await Handler.HandleAsync(Part(original), CancellationToken.None);

        Assert.True(XNode.DeepEquals(XElement.Parse(original), XElement.Parse(result.Text)));
    }

    [Fact]
    public async Task The_reduction_is_an_ordered_subsequence_of_the_input()
    {
        var original = "<root>\n  <a>1</a>\n  <b>keep me</b>\n</root>";

        var result = await Handler.HandleAsync(Part(original), CancellationToken.None);

        OrderedSubsequence.AssertHolds(result.Text, original);
    }

    [Fact]
    public async Task A_document_that_does_not_parse_falls_back_to_the_line_rule()
    {
        var result = await Handler.HandleAsync(Part("<root><a>"), CancellationToken.None);

        Assert.Equal("xml.dedupe", result.Path);
    }

    [Fact]
    public Task The_handler_claims_the_xml_kind_and_nothing_else()
    {
        try
        {
            Assert.True(Handler.CanHandle(ContentType.Xml));
            Assert.False(Handler.CanHandle(ContentType.Json));
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    [Fact]
    public async Task The_fallback_drops_only_a_line_that_repeats_the_line_before_it()
    {
        var result = await Handler.HandleAsync(Part("<root\n  <a>1</a>\n  <a>1</a>\n  <b>2</b>\n  <a>1</a>\n<a>1</a>"), CancellationToken.None);

        Assert.Equal("xml.dedupe", result.Path);
        Assert.Equal(["<root", "  <a>1</a>", "  <b>2</b>", "  <a>1</a>", "<a>1</a>"], result.Text.Split('\n'));
    }
}
