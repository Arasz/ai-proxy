using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>The JSON reducer, stated as behaviour: the document gets smaller and keeps its values.</summary>
public class JsonCompressionHandlerTests
{
    private static readonly JsonCompressionHandler Handler = new();

    private static ContentPart Part(string text) => new("request", 1, ContentType.Json, text);

    [Fact]
    public async Task The_whitespace_goes_away()
    {
        var result = await Handler.HandleAsync(Part("""{ "a": 1, "b": [1, 2] }"""), CancellationToken.None);

        Assert.Equal("""{"a":1,"b":[1,2]}""", result.Text);
    }

    [Fact]
    public async Task The_scale_of_a_number_survives()
    {
        var result = await Handler.HandleAsync(Part("""{ "price": 49.90 }"""), CancellationToken.None);

        Assert.Contains("49.90", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_values_of_the_document_survive()
    {
        var original = """{ "a": 1, "b": [1, 2], "c": { "d": "text" } }""";

        var result = await Handler.HandleAsync(Part(original), CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(result.Text)));
    }

    [Fact]
    public async Task A_document_that_does_not_parse_falls_back_to_the_line_rule()
    {
        var result = await Handler.HandleAsync(Part("{ this is not json"), CancellationToken.None);

        Assert.Equal("json.dedupe", result.Path);
    }

    [Fact]
    public async Task The_handler_says_which_path_it_took()
    {
        var result = await Handler.HandleAsync(Part("""{"a":1}"""), CancellationToken.None);

        Assert.Equal("json.compact", result.Path);
    }

    [Fact]
    public async Task The_handler_claims_the_json_kind_and_nothing_else()
    {
        Assert.True(Handler.CanHandle(ContentType.Json));
        Assert.False(Handler.CanHandle(ContentType.Xml));
    }
}
