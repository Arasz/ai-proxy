using System.Text.Json.Nodes;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>The JSON reducer, stated as behaviour: the document gets smaller and keeps its values.</summary>
public sealed class JsonCompressionHandlerTests
{
    private static readonly JsonCompressionHandler Handler = new();

    private static ContentPart Part(string text)
    {
        return new ContentPart("request", 1, ContentType.Json, text);
    }

    [Fact]
    public async Task The_whitespace_goes_away()
    {
        var result = await Handler.HandleAsync(Part("""{ "a": 1, "b": [1, 2] }"""), CancellationToken.None);

        Assert.Equal("""{"a":1,"b":[1,2]}""", result.Text);
    }

    [Theory]
    [InlineData("""{"title":"fix: don't drop it"}""")]
    [InlineData("""{"name":"Rafał","note":"a < b && c > d","sum":"1+1"}""")]
    [InlineData("""{"mood":"shipped 😀"}""")]
    public async Task Compact_json_is_never_written_back_longer(string compact)
    {
        var result = await Handler.HandleAsync(Part(compact), CancellationToken.None);

        Assert.Equal(compact, result.Text);
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
    public async Task The_reduction_is_an_ordered_subsequence_of_the_input()
    {
        var original = "{ \"a\": 1, \"b\": [1, 2], \"c\": { \"d\": \"text\" } }";

        var result = await Handler.HandleAsync(Part(original), CancellationToken.None);

        OrderedSubsequence.AssertHolds(result.Text, original);
    }

    [Fact]
    public async Task The_handler_says_which_path_it_took()
    {
        var result = await Handler.HandleAsync(Part("""{"a":1}"""), CancellationToken.None);

        Assert.Equal("json.compact", result.Path);
    }

    [Fact]
    public Task The_handler_claims_the_json_kind_and_nothing_else()
    {
        try
        {
            Assert.True(Handler.CanHandle(ContentType.Json));
            Assert.False(Handler.CanHandle(ContentType.Xml));
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    [Fact]
    public async Task A_lone_surrogate_escape_falls_back_to_the_line_rule()
    {
        var result = await Handler.HandleAsync(Part("{\"a\":\"x\\ud800y\"}"), CancellationToken.None);

        Assert.Equal("json.dedupe", result.Path);
        Assert.Equal("{\"a\":\"x\\ud800y\"}", result.Text);
    }

    [Fact]
    public async Task The_fallback_drops_only_a_line_that_repeats_the_line_before_it()
    {
        var result = await Handler.HandleAsync(Part("{ broken\n  \"a\": 1,\n  \"a\": 1,\n  \"b\": 2,\n  \"a\": 1,\n\"a\": 1,"), CancellationToken.None);

        Assert.Equal("json.dedupe", result.Path);
        Assert.Equal(["{ broken", "  \"a\": 1,", "  \"b\": 2,", "  \"a\": 1,", "\"a\": 1,"], result.Text.Split('\n'));
    }
}
