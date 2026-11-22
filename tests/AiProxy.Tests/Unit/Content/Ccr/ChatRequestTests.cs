using System.Text;
using System.Text.Json;
using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

public sealed class ChatRequestTests
{
    private const string Tool = """{"type":"function","function":{"name":"read","parameters":{"type":"object"}}}""";

    private static string Body(string extra = "", string model = "deepseek/deepseek-v4", string tools = $"[{Tool}]")
    {
        return $$"""{"model":"{{model}}","stream":true,"tools":{{tools}}{{extra}},"messages":[{"role":"user","content":"hi"}]}""";
    }

    private static ChatRequest Read(string body)
    {
        return ChatRequest.Read(Encoding.UTF8.GetBytes(body))!;
    }

    [Theory]
    [InlineData("deepseek/deepseek-v4", true)]
    [InlineData("anthropic/claude-x", true)]
    [InlineData("openai/gpt-x", true)]
    [InlineData("google/gemini-x", true)]
    [InlineData("openrouter/auto", false)]
    [InlineData("mistralai/mixtral", false)]
    [InlineData("", false)]
    public void Only_models_with_a_known_prefix_qualify(string model, bool qualifies)
    {
        Assert.Equal(qualifies, Read(Body(model: model)).Qualifies);
    }

    [Theory]
    [InlineData(",\"n\":2", false)]
    [InlineData(",\"n\":1", true)]
    [InlineData(",\"tool_choice\":\"none\"", false)]
    [InlineData(",\"tool_choice\":\"auto\"", true)]
    [InlineData(",\"tool_choice\":\"required\"", true)]
    [InlineData(",\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"read\"}}", false)]
    [InlineData(",\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"proxy_retrieve\"}}", true)]
    public void A_choice_count_or_tool_choice_the_loop_cannot_serve_disqualifies_the_request(string extra, bool qualifies)
    {
        Assert.Equal(qualifies, Read(Body(extra)).Qualifies);
    }

    [Fact]
    public void A_request_without_stream_or_tools_does_not_qualify()
    {
        Assert.False(Read("""{"model":"deepseek/x","tools":[{"type":"function","function":{"name":"read"}}],"messages":[]}""").Qualifies);
        Assert.False(Read(Body(tools: "[]")).Qualifies);
        Assert.False(Read("""{"model":"deepseek/x","stream":true,"messages":[]}""").Qualifies);
    }

    [Fact]
    public void The_usage_option_is_read_from_stream_options()
    {
        Assert.False(Read(Body()).IncludeUsage);
        Assert.True(Read(Body(",\"stream_options\":{\"include_usage\":true}")).IncludeUsage);
        Assert.False(Read(Body(",\"stream_options\":{\"include_usage\":false}")).IncludeUsage);
    }

    [Fact]
    public void WithTool_appends_the_function_tool_last_and_asks_for_usage_leaving_the_other_bytes_alone()
    {
        var body = Body();

        var prepared = Encoding.UTF8.GetString(ChatRequest.WithTool(Encoding.UTF8.GetBytes(body))!.Body);

        using var document = JsonDocument.Parse(prepared);
        var tools = document.RootElement.GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.Equal("read", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("function", tools[1].GetProperty("type").GetString());
        Assert.Equal(CcrRetrieval.Name, tools[1].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tools[1].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        var expected = body.Insert(body.IndexOf("],\"messages\"", StringComparison.Ordinal), "," + CcrRetrieval.ChatDefinition)[..^1] + ",\"stream_options\":{\"include_usage\":true}}";
        Assert.Equal(expected, prepared);
    }

    [Fact]
    public void WithTool_keeps_a_proxy_retrieve_tool_the_request_already_has_and_a_usage_option_already_set()
    {
        var already = Body(",\"stream_options\":{\"include_usage\":true}", tools: $"[{Tool},{CcrRetrieval.ChatDefinition}]");

        var prepared = ChatRequest.WithTool(Encoding.UTF8.GetBytes(already))!.Body;

        Assert.Equal(already, Encoding.UTF8.GetString(prepared));
    }

    [Fact]
    public void WithTool_turns_on_a_usage_option_the_client_set_to_false_and_keeps_its_other_stream_options()
    {
        var body = Body(",\"stream_options\":{\"include_usage\":false,\"other\":1}");

        using var document = JsonDocument.Parse(ChatRequest.WithTool(Encoding.UTF8.GetBytes(body))!.Body);

        var options = document.RootElement.GetProperty("stream_options");
        Assert.True(options.GetProperty("include_usage").GetBoolean());
        Assert.Equal(1, options.GetProperty("other").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("tools").GetArrayLength());
    }

    [Theory]
    [InlineData("{}", "{\"include_usage\":true}")]
    [InlineData("{ \"other\" : 1 }", "{\"include_usage\":true, \"other\" : 1 }")]
    [InlineData("{ \"include_usage\" : false }", "{ \"include_usage\" : true }")]
    [InlineData("null", "{\"include_usage\":true}")]
    public void WithTool_changes_only_the_stream_options_span_when_usage_is_missing_or_off(string options, string expectedOptions)
    {
        // The body uses forms System.Text.Json rewrites: extra whitespace, an escaped
        // character and an exponent number. Every byte outside the span must survive.
        const string Before = "{ \"model\" : \"deepseek/x\",  \"temperature\": 1.0E2, \"note\": \"caf\\u00e9\", \"stream\" : true,\n";
        var after = $", \"tools\" : [{Tool},{CcrRetrieval.ChatDefinition}], \"messages\" : [] }}";
        var body = $"{Before}\"stream_options\": {options}{after}";

        var prepared = Encoding.UTF8.GetString(ChatRequest.WithTool(Encoding.UTF8.GetBytes(body))!.Body);

        Assert.Equal($"{Before}\"stream_options\": {expectedOptions}{after}", prepared);
    }

    [Fact]
    public void WithTool_uses_the_last_of_two_stream_options_keys()
    {
        var body = $"{{\"stream\":true,\"stream_options\":{{\"include_usage\":true}},\"stream_options\":{{\"x\":1}},\"tools\":[{Tool}],\"messages\":[]}}";

        var prepared = Encoding.UTF8.GetString(ChatRequest.WithTool(Encoding.UTF8.GetBytes(body))!.Body);

        Assert.Contains("\"stream_options\":{\"include_usage\":true,\"x\":1}", prepared);
    }

    [Fact]
    public void WithTool_ignores_stream_options_inside_messages()
    {
        const string Messages = "\"messages\":[{\"role\":\"user\",\"content\":\"\\\"stream_options\\\"\",\"stream_options\":{\"a\":1}}]";
        var body = $"{{\"stream\":true,\"tools\":[{Tool},{CcrRetrieval.ChatDefinition}],{Messages}}}";

        var prepared = Encoding.UTF8.GetString(ChatRequest.WithTool(Encoding.UTF8.GetBytes(body))!.Body);

        Assert.Equal($"{{\"stream\":true,\"tools\":[{Tool},{CcrRetrieval.ChatDefinition}],{Messages},\"stream_options\":{{\"include_usage\":true}}}}", prepared);
    }

    [Theory]
    [InlineData(",\"n\":null", true)]
    [InlineData(",\"n\":\"2\"", false)]
    [InlineData(",\"n\":2.5", false)]
    [InlineData(",\"tool_choice\":null", true)]
    [InlineData(",\"tool_choice\":{\"function\":null}", false)]
    [InlineData(",\"tool_choice\":{\"function\":{\"name\":5}}", false)]
    [InlineData(",\"tool_choice\":{\"function\":{\"name\":null}}", false)]
    [InlineData(",\"stream_options\":null", true)]
    [InlineData(",\"stream_options\":{\"include_usage\":null}", true)]
    public void A_null_or_wrongly_typed_field_never_throws_and_qualifies_only_when_the_loop_can_serve_it(string extra, bool qualifies)
    {
        Assert.Equal(qualifies, Read(Body(extra)).Qualifies);
    }

    [Fact]
    public void A_model_that_is_not_a_string_does_not_qualify()
    {
        Assert.False(Read("""{"model":5,"stream":true,"tools":[{"type":"function","function":{"name":"read"}}],"messages":[]}""").Qualifies);
    }

    [Theory]
    [InlineData("""{"model":"deepseek/x","stream":true,"tools":[],"messages":[]}""")]
    [InlineData("""{"model":"deepseek/x","stream":true,"messages":[]}""")]
    [InlineData("""{"model":"deepseek/x","stream":true,"tools":[{"type":"function","function":{"name":"read"}}]""")]
    public void WithTool_returns_null_when_it_cannot_append_the_tool(string body)
    {
        Assert.Null(ChatRequest.WithTool(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void WithTool_reports_only_the_characters_of_the_tool_it_appended_not_the_usage_option()
    {
        var body = Encoding.UTF8.GetBytes(Body());

        Assert.Equal(CcrRetrieval.ChatDefinition.Length + 1, ChatRequest.WithTool(body)!.ToolCharacters);
        Assert.Equal(0, ChatRequest.WithTool(Encoding.UTF8.GetBytes(Body(tools: $"[{Tool},{CcrRetrieval.ChatDefinition}]")))!.ToolCharacters);
    }
}
