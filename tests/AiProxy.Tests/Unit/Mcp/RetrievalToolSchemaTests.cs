using System.Text.Json;
using AiProxy.Ccr;
using AiProxy.Mcp;

namespace AiProxy.Tests.Unit.Mcp;

/// <summary>The schema the MCP tool publishes is typed out here, and the API validator agrees with each bound.</summary>
public sealed class RetrievalToolSchemaTests
{
    private static JsonElement Schema => RetrievalTools.CreateTool().ProtocolTool.InputSchema;

    private static JsonElement Property(string name)
    {
        return Schema.GetProperty("properties").GetProperty(name);
    }

    [Fact]
    public void Mode_is_the_enum_of_the_three_selections()
    {
        var values = Property("mode").GetProperty("enum").EnumerateArray().Select(static value => value.GetString()).ToArray();

        Assert.Equal(["lines", "pattern", "whole"], values);
    }

    [Fact]
    public void Context_is_an_integer_from_0_to_10()
    {
        var context = Property("context");

        Assert.Equal("integer", context.GetProperty("type").GetString());
        Assert.Equal(0, context.GetProperty("minimum").GetInt32());
        Assert.Equal(10, context.GetProperty("maximum").GetInt32());
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    public void Line_bounds_are_integers_of_at_least_1(string name)
    {
        var property = Property(name);

        Assert.Equal("integer", property.GetProperty("type").GetString());
        Assert.Equal(1, property.GetProperty("minimum").GetInt32());
    }

    [Fact]
    public void Id_is_16_lowercase_hex_characters()
    {
        Assert.Equal("^[0-9a-f]{16}$", Property("id").GetProperty("pattern").GetString());
    }

    [Fact]
    public void Id_and_mode_are_required_and_nothing_else_is_allowed()
    {
        var required = Schema.GetProperty("required").EnumerateArray().Select(static value => value.GetString()).ToArray();

        Assert.Equal(["id", "mode"], required);
        Assert.False(Schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData("""{"id":"0123456789abcdef","mode":"lines"}""", true)]
    [InlineData("""{"id":"0123456789abcdef","mode":"pattern"}""", true)]
    [InlineData("""{"id":"0123456789abcdef","mode":"whole"}""", true)]
    [InlineData("""{"id":"0123456789abcdef","mode":"Whole"}""", false)]
    [InlineData("""{"id":"0123456789abcde","mode":"whole"}""", false)]
    [InlineData("""{"id":"0123456789abcdef0","mode":"whole"}""", false)]
    [InlineData("""{"id":"0123456789abcdeg","mode":"whole"}""", false)]
    [InlineData("""{"id":"0123456789abcdef","mode":"whole","context":0}""", true)]
    [InlineData("""{"id":"0123456789abcdef","mode":"whole","context":10}""", true)]
    [InlineData("""{"id":"0123456789abcdef","mode":"whole","context":-1}""", false)]
    [InlineData("""{"id":"0123456789abcdef","mode":"whole","context":11}""", false)]
    [InlineData("""{"id":"0123456789abcdef","mode":"lines","start":1}""", true)]
    [InlineData("""{"id":"0123456789abcdef","mode":"lines","start":0}""", false)]
    [InlineData("""{"id":"0123456789abcdef","mode":"lines","end":1}""", true)]
    [InlineData("""{"id":"0123456789abcdef","mode":"lines","end":0}""", false)]
    public void The_api_validator_accepts_each_bound_and_rejects_one_past_it(string arguments, bool accepted)
    {
        using var document = JsonDocument.Parse(arguments);

        var violation = new RetrievalArgumentSchema().Violation(document.RootElement);

        Assert.Equal(accepted, violation is null);
    }

    [Fact]
    public void The_api_error_for_a_bad_mode_names_the_allowed_values()
    {
        using var document = JsonDocument.Parse("""{"id":"0123456789abcdef","mode":"everything"}""");

        var violation = new RetrievalArgumentSchema().Violation(document.RootElement);

        Assert.Contains("lines", violation, StringComparison.Ordinal);
        Assert.Contains("pattern", violation, StringComparison.Ordinal);
        Assert.Contains("whole", violation, StringComparison.Ordinal);
    }
}
