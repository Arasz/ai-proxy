using System.Text.Json;
using CodeReducerCore;

namespace AiProxy.Tests.Unit.Content.CodeAdapter;

public sealed class CodeAdapterTests
{
    [Fact]
    public async Task Malformed_input_fails_without_publishing_a_view()
    {
        using var input = new StringReader("{");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await CodeReducerCommand.RunAsync(input, output, error);

        Assert.NotEqual(0, exit);
        Assert.Equal("", output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task Defaults_invoke_the_actual_reducer_and_publish_only_the_contract_fields()
    {
        var source = $"// {new string('x', 100)}\nclass Example {{ }}\n\n";
        var request = JsonSerializer.Serialize(new { text = source, filePath = "Example.cs", retrievalHint = "[fixture retrieve before editing]" });
        using var input = new StringReader(request);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await CodeReducerCommand.RunAsync(input, output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        Assert.NotEmpty(output.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(["text", "changed", "supported"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("class Example { }\n[fixture retrieve before editing]", document.RootElement.GetProperty("text").GetString());
        Assert.True(document.RootElement.GetProperty("changed").GetBoolean());
        Assert.True(document.RootElement.GetProperty("supported").GetBoolean());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":1}")]
    [InlineData("{\"text\":\"code\",\"filePath\":1}")]
    [InlineData("{\"text\":\"code\",\"retrievalHint\":true}")]
    [InlineData("{\"text\":\"code\",\"removeComments\":\"false\"}")]
    [InlineData("{\"text\":\"code\",\"removeImports\":null}")]
    [InlineData("{\"text\":\"code\",\"renameIdentifiers\":1}")]
    [InlineData("{\"text\":\"code\",\"extra\":true}")]
    [InlineData("{\"text\":\"first\",\"text\":\"second\"}")]
    [InlineData("{\"text\":\"code\",\"removeComments\":true,\"removeComments\":false}")]
    public async Task Invalid_contract_is_rejected_without_a_view(string request)
    {
        using var input = new StringReader(request);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await CodeReducerCommand.RunAsync(input, output, error);

        Assert.NotEqual(0, exit);
        Assert.Equal("", output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Explicit_options_reach_the_actual_core(bool removeComments, bool removeImports, bool renameIdentifiers)
    {
        var source = $"// retained comment\nusing Example;\nint originalLongName = 1;\noriginalLongName += originalLongName;\noriginalLongName++;\n{new string('\n', 100)}";
        var request = JsonSerializer.Serialize(new { text = source, filePath = "Example.cs", retrievalHint = "[fixture hint]", removeComments, removeImports, renameIdentifiers });
        using var input = new StringReader(request);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await CodeReducerCommand.RunAsync(input, output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var view = document.RootElement.GetProperty("text").GetString()!;
        Assert.True(document.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(!removeComments, view.Contains("// retained comment", StringComparison.Ordinal));
        Assert.Equal(!removeImports, view.Contains("using Example;", StringComparison.Ordinal));
        Assert.Equal(renameIdentifiers, view.Contains("a:originalLongName", StringComparison.Ordinal));
        Assert.Contains(renameIdentifiers ? "int a = 1;" : "int originalLongName = 1;", view);
        Assert.EndsWith("[fixture hint]", view);
    }

    [Theory]
    [InlineData(null, "[fixture hint]")]
    [InlineData("Example.js", "[fixture hint]")]
    [InlineData("Example.cs", null)]
    [InlineData("Example.cs", "")]
    public async Task Missing_context_returns_the_exact_original(string? filePath, string? retrievalHint)
    {
        var source = $"// {new string('x', 100)}\r\nclass Example {{ }}\r\n\r\n";
        var request = JsonSerializer.Serialize(new { text = source, filePath, retrievalHint });
        using var input = new StringReader(request);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await CodeReducerCommand.RunAsync(input, output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(source, document.RootElement.GetProperty("text").GetString());
        Assert.False(document.RootElement.GetProperty("changed").GetBoolean());
        Assert.False(document.RootElement.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task Optional_fields_may_be_omitted_and_the_original_is_returned()
    {
        using var input = new StringReader("{\"text\":\"original\"}");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await CodeReducerCommand.RunAsync(input, output, error);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("original", document.RootElement.GetProperty("text").GetString());
        Assert.False(document.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal("", error.ToString());
    }
}