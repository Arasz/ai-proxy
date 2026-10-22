using System.Xml.Linq;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>The XML reducer, stated as behaviour: the indentation goes and the text stays.</summary>
public class XmlCompressionHandlerTests
{
    private static readonly XmlCompressionHandler Handler = new();

    private static ContentPart Part(string text) => new("request", 1, ContentType.Xml, text);

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
    public async Task The_values_of_the_document_survive()
    {
        var original = "<root><a>1</a><b>keep me</b></root>";

        var result = await Handler.HandleAsync(Part(original), CancellationToken.None);

        Assert.True(XNode.DeepEquals(XElement.Parse(original), XElement.Parse(result.Text)));
    }

    [Fact]
    public async Task A_document_that_does_not_parse_falls_back_to_the_line_rule()
    {
        var result = await Handler.HandleAsync(Part("<root><a>"), CancellationToken.None);

        Assert.Equal("xml.dedupe", result.Path);
    }

    [Fact]
    public async Task The_handler_claims_the_xml_kind_and_nothing_else()
    {
        Assert.True(Handler.CanHandle(ContentType.Xml));
        Assert.False(Handler.CanHandle(ContentType.Json));
    }
}
