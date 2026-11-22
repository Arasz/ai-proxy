using System.Text;
using AiProxy.Ccr;
using AiProxy.Configuration;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Ccr;

public sealed class CcrChatPlanTests
{
    private const string Chat = "/api/v1/chat/completions";
    private const string Stream = """{"model":"deepseek/deepseek-v4.1-flash-1017","stream":true,"tools":[{"type":"function","function":{"name":"read"}}],"messages":[]}""";

    private static Task<CcrPlan> DecideAsync(string body, bool chatHost = true, string path = Chat, string method = "POST", CcrSettings? settings = null, Dialect dialect = Dialect.OpenAi)
    {
        return CcrPlan.DecideAsync(settings ?? new CcrSettings(), null!, dialect, chatHost, method, path, Encoding.UTF8.GetBytes(body), CancellationToken.None);
    }

    [Fact]
    public async Task A_streamed_request_with_tools_to_a_known_model_on_a_chat_host_gets_a_stateless_chat_plan()
    {
        var plan = await DecideAsync(Stream);

        Assert.True(plan.Relays);
        Assert.Equal(CcrWire.Chat, plan.Wire);
        Assert.Equal(CcrMode.Stateless, plan.Mode);
        Assert.False(plan.ClientUsage);
        Assert.True((await DecideAsync(Stream.Replace("\"stream\":true", "\"stream\":true,\"stream_options\":{\"include_usage\":true}", StringComparison.Ordinal))).ClientUsage);
    }

    [Fact]
    public void Anthropic_plans_are_on_the_messages_wire()
    {
        Assert.Equal(CcrWire.Messages, CcrPlan.Stateless.Wire);
        Assert.Equal(CcrWire.Messages, CcrPlan.Create.Wire);
        Assert.Equal(CcrWire.Messages, CcrPlan.Continue("m").Wire);
    }

    [Fact]
    public async Task A_host_that_does_not_serve_chat_ccr_is_forwarded()
    {
        Assert.Equal(CcrPlan.Forward, await DecideAsync(Stream, chatHost: false));
    }

    [Theory]
    [InlineData("""{"model":"openrouter/auto","stream":true,"tools":[{"type":"function","function":{"name":"read"}}],"messages":[]}""")]
    [InlineData("""{"model":"unknown/model","stream":true,"tools":[{"type":"function","function":{"name":"read"}}],"messages":[]}""")]
    [InlineData("""{"model":"deepseek/x","stream":true,"n":2,"tools":[{"type":"function","function":{"name":"read"}}],"messages":[]}""")]
    [InlineData("""{"model":"deepseek/x","tools":[{"type":"function","function":{"name":"read"}}],"messages":[]}""")]
    [InlineData("""{"model":"deepseek/x","stream":true,"messages":[]}""")]
    [InlineData("""{"model":"deepseek/x","stream":true,"tool_choice":"none","tools":[{"type":"function","function":{"name":"read"}}],"messages":[]}""")]
    [InlineData("not json")]
    public async Task A_request_the_loop_cannot_serve_is_forwarded(string body)
    {
        Assert.Equal(CcrPlan.Forward, await DecideAsync(body));
    }

    [Fact]
    public async Task Only_a_post_to_a_chat_completions_path_is_planned()
    {
        Assert.Equal(CcrPlan.Forward, await DecideAsync(Stream, path: "/api/v1/embeddings"));
        Assert.Equal(CcrPlan.Forward, await DecideAsync(Stream, method: "GET"));
        Assert.Equal(CcrWire.Chat, (await DecideAsync(Stream, path: "/v1/chat/completions/")).Wire);
    }

    [Fact]
    public async Task Switching_ccr_off_forwards_chat_requests_too()
    {
        Assert.Equal(CcrPlan.Forward, await DecideAsync(Stream, settings: new CcrSettings { Enabled = false }));
    }

    [Fact]
    public async Task A_gemini_request_is_forwarded()
    {
        Assert.Equal(CcrPlan.Forward, await DecideAsync(Stream, dialect: Dialect.Gemini));
    }

    [Fact]
    public void The_catalog_serves_chat_ccr_on_openrouter_only()
    {
        Assert.True(ProviderHostCatalog.Lookup("openrouter.ai")!.ChatCcr);
        foreach (var host in new[] { "api.openai.com", "eu.openrouter.ai", "chatgpt.com", "api.anthropic.com" })
        {
            Assert.False(ProviderHostCatalog.Lookup(host)!.ChatCcr, host);
        }
    }
    [Fact]
    public async Task A_chat_plan_appends_the_chat_tool_and_counts_its_characters()
    {
        var plan = await DecideAsync(Stream);

        var request = plan.WithTool(Encoding.UTF8.GetBytes(Stream))!;

        Assert.Contains(CcrRetrieval.ChatDefinition, Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
        Assert.Equal(CcrRetrieval.ChatDefinition.Length + 1, request.ToolCharacters);
    }

    [Fact]
    public void A_messages_plan_appends_the_messages_tool_and_counts_its_characters()
    {
        var body = Encoding.UTF8.GetBytes("""{"stream":true,"tools":[{"name":"read"}],"messages":[]}""");

        var request = CcrPlan.Stateless.WithTool(body)!;

        Assert.DoesNotContain(CcrRetrieval.ChatDefinition, Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
        Assert.Contains(CcrRetrieval.Name, Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
        Assert.Equal(request.Body.Length - body.Length, request.ToolCharacters);
    }

    [Fact]
    public void A_body_without_tools_gets_no_tool_request()
    {
        Assert.Null(CcrPlan.Stateless.WithTool(Encoding.UTF8.GetBytes("""{"stream":true,"messages":[]}""")));
    }
}
