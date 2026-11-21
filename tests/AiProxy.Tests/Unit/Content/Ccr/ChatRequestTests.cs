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
    public void Prepare_appends_the_function_tool_last_and_asks_for_usage_leaving_the_other_bytes_alone()
    {
        var body = Body();

        var prepared = Encoding.UTF8.GetString(ChatRequest.Prepare(Encoding.UTF8.GetBytes(body)));

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
    public void Prepare_keeps_a_proxy_retrieve_tool_the_request_already_has_and_a_usage_option_already_set()
    {
        var already = Body(",\"stream_options\":{\"include_usage\":true}", tools: $"[{Tool},{CcrRetrieval.ChatDefinition}]");

        var prepared = ChatRequest.Prepare(Encoding.UTF8.GetBytes(already));

        Assert.Equal(already, Encoding.UTF8.GetString(prepared));
    }

    [Fact]
    public void Prepare_turns_on_a_usage_option_the_client_set_to_false_and_keeps_its_other_stream_options()
    {
        var body = Body(",\"stream_options\":{\"include_usage\":false,\"other\":1}");

        using var document = JsonDocument.Parse(ChatRequest.Prepare(Encoding.UTF8.GetBytes(body)));

        var options = document.RootElement.GetProperty("stream_options");
        Assert.True(options.GetProperty("include_usage").GetBoolean());
        Assert.Equal(1, options.GetProperty("other").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("tools").GetArrayLength());
    }
}
