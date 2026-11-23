using System.Text.Json;
using AiProxy.Ccr;
using AiProxy.Core;
using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Ccr;

/// <summary>The proxy_retrieve arguments are checked against the input_schema the model is sent, not a copy of it.</summary>
public sealed class RetrievalArgumentSchemaTests
{
    private static readonly RetrievalArgumentSchema Schema = new();

    private static Validation Check(string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return Schema.Check(document.RootElement);
    }

    [Theory]
    [InlineData("""{"id":"0123456789abcdef","mode":"whole"}""")]
    [InlineData("""{"id":"0123456789abcdef","mode":"lines","start":1,"end":2}""")]
    [InlineData("""{"id":"0123456789abcdef","mode":"pattern","pattern":"x","context":10}""")]
    public void Arguments_that_follow_the_tool_schema_pass(string arguments) => Assert.IsType<Valid>(Check(arguments).Value);

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"mode":"whole"}""")]
    [InlineData("""{"id":"0123456789ABCDEF","mode":"whole"}""")]
    [InlineData("""{"id":"0123456789abcdef","mode":"everything"}""")]
    [InlineData("""{"id":"0123456789abcdef","mode":"whole","extra":1}""")]
    [InlineData("""{"id":"0123456789abcdef","mode":"lines","start":"1","end":2}""")]
    [InlineData("""{"id":"0123456789abcdef","mode":"lines","start":0,"end":2}""")]
    [InlineData("""{"id":"0123456789abcdef","mode":"pattern","pattern":"x","context":11}""")]
    public void Arguments_that_break_the_tool_schema_name_the_violation(string arguments)
    {
        var invalid = Assert.IsType<Invalid>(Check(arguments).Value);

        Assert.False(string.IsNullOrWhiteSpace(invalid.Reason));
    }

    [Fact]
    public void A_bad_enum_value_is_invalid_and_names_the_property_and_the_allowed_values()
    {
        var invalid = Assert.IsType<Invalid>(Check("""{"id":"0123456789abcdef","mode":"everything"}""").Value);

        Assert.Contains("/mode", invalid.Reason, StringComparison.Ordinal);
        Assert.Contains("Allowed values", invalid.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_is_the_one_in_the_tool_definition()
    {
        using var definition = JsonDocument.Parse(CcrRetrieval.Definition);

        Assert.Equal(definition.RootElement.GetProperty("input_schema").GetRawText(), RetrievalArgumentSchema.Text);
    }
}