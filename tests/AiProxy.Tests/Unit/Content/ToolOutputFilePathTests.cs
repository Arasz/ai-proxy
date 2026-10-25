using System.Text;
using AiProxy.Core.Content;

namespace AiProxy.Tests.Unit.Content;

/// <summary>
/// The file path of a region, stated as behaviour: a tool result carries the path of the
/// whole file its call read, matched by call id, and no path when the match is in doubt.
/// </summary>
public sealed class ToolOutputFilePathTests
{
    private static readonly ToolOutputRegionFinder Finder = new();

    private static ContentRegion[] Find(string body) => Finder.Find(Encoding.UTF8.GetBytes(body)).ToArray();

    private static string AnthropicCall(string id, string input) =>
        $$"""{"type":"tool_use","id":"{{id}}","name":"Read","input":{{input}}}""";

    private static string AnthropicResult(string id, string text) =>
        $$"""{"type":"tool_result","tool_use_id":"{{id}}","content":"{{text}}"}""";

    private static string AnthropicBody(string calls, string results) =>
        $$"""{"messages":[{"role":"assistant","content":[{{calls}}]},{"role":"user","content":[{{results}}]}]}""";

    [Fact]
    public void Two_anthropic_results_in_reversed_order_each_carry_the_path_of_their_own_call()
    {
        var body = AnthropicBody(
            $"{AnthropicCall("call-a", """{"file_path":"src/A.cs"}""")},{AnthropicCall("call-b", """{"file_path":"src/b.py"}""")}",
            $"{AnthropicResult("call-b", "content of b")},{AnthropicResult("call-a", "content of a")}");

        var regions = Find(body);

        Assert.Equal(2, regions.Length);
        Assert.Equal(("content of b", "src/b.py"), (regions[0].Text, regions[0].FilePath));
        Assert.Equal(("content of a", "src/A.cs"), (regions[1].Text, regions[1].FilePath));
    }

    [Fact]
    public void Two_openai_chat_results_in_reversed_order_each_carry_the_path_of_their_own_call()
    {
        var body = """
            {"messages":[
              {"role":"assistant","tool_calls":[
                {"id":"call-a","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"src/A.cs\"}"}},
                {"id":"call-b","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"src/b.ts\"}"}}]},
              {"role":"tool","tool_call_id":"call-b","content":"content of b"},
              {"role":"tool","tool_call_id":"call-a","content":"content of a"}]}
            """;

        var regions = Find(body);

        Assert.Equal(2, regions.Length);
        Assert.Equal(("content of b", "src/b.ts"), (regions[0].Text, regions[0].FilePath));
        Assert.Equal(("content of a", "src/A.cs"), (regions[1].Text, regions[1].FilePath));
    }

    [Fact]
    public void Two_openai_responses_outputs_in_reversed_order_each_carry_the_path_of_their_own_call()
    {
        var body = """
            {"input":[
              {"type":"function_call","call_id":"call-a","name":"read_file","arguments":"{\"filePath\":\"src/A.cs\"}"},
              {"type":"function_call","call_id":"call-b","name":"read_file","arguments":"{\"filePath\":\"src/b.js\"}"},
              {"type":"function_call_output","call_id":"call-b","output":"content of b"},
              {"type":"function_call_output","call_id":"call-a","output":"content of a"}]}
            """;

        var regions = Find(body);

        Assert.Equal(2, regions.Length);
        Assert.Equal(("content of b", "src/b.js"), (regions[0].Text, regions[0].FilePath));
        Assert.Equal(("content of a", "src/A.cs"), (regions[1].Text, regions[1].FilePath));
    }

    [Theory]
    [InlineData("""{"file_path":"src/A.cs","offset":10,"limit":20}""")]
    [InlineData("""{"file_path":"src/A.cs","limit":20}""")]
    [InlineData("""{"path":"src/A.cs","start_line":1,"end_line":40}""")]
    public void A_partial_read_carries_no_path(string input)
    {
        var body = AnthropicBody(AnthropicCall("call-a", input), AnthropicResult("call-a", "part of a"));

        Assert.Null(Assert.Single(Find(body)).FilePath);
    }

    [Theory]
    [InlineData("""{"file_path":"src/A.cs","path":"src/B.cs"}""")]
    [InlineData("""{"paths":["src/A.cs","src/B.cs"]}""")]
    [InlineData("""{"file_path":""}""")]
    [InlineData("""{"file_path":7}""")]
    [InlineData("""{}""")]
    public void A_call_without_exactly_one_explicit_path_carries_no_path(string input)
    {
        var body = AnthropicBody(AnthropicCall("call-a", input), AnthropicResult("call-a", "content"));

        Assert.Null(Assert.Single(Find(body)).FilePath);
    }

    [Fact]
    public void A_result_whose_call_is_missing_carries_no_path()
    {
        var body = AnthropicBody(
            AnthropicCall("call-a", """{"file_path":"src/A.cs"}"""),
            AnthropicResult("call-z", "content"));

        Assert.Null(Assert.Single(Find(body)).FilePath);
    }

    [Fact]
    public void A_result_without_a_call_id_carries_no_path()
    {
        var body = """{"messages":[{"role":"tool","content":"content"}]}""";

        Assert.Null(Assert.Single(Find(body)).FilePath);
    }

    [Fact]
    public void A_call_id_used_by_two_calls_carries_no_path()
    {
        var body = AnthropicBody(
            $"{AnthropicCall("call-a", """{"file_path":"src/A.cs"}""")},{AnthropicCall("call-a", """{"file_path":"src/B.cs"}""")}",
            AnthropicResult("call-a", "content"));

        Assert.Null(Assert.Single(Find(body)).FilePath);
    }

    [Fact]
    public void A_call_id_answered_by_two_results_carries_no_path()
    {
        var body = AnthropicBody(
            AnthropicCall("call-a", """{"file_path":"src/A.cs"}"""),
            $"{AnthropicResult("call-a", "first")},{AnthropicResult("call-a", "second")}");

        Assert.All(Find(body), region => Assert.Null(region.FilePath));
    }

    [Fact]
    public void Arguments_that_do_not_parse_carry_no_path()
    {
        var body = """
            {"messages":[
              {"role":"assistant","tool_calls":[
                {"id":"call-a","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"src/A.cs\""}}]},
              {"role":"tool","tool_call_id":"call-a","content":"content"}]}
            """;

        Assert.Null(Assert.Single(Find(body)).FilePath);
    }

    [Fact]
    public void A_result_split_over_two_text_blocks_carries_no_path()
    {
        var body = AnthropicBody(
            AnthropicCall("call-a", """{"file_path":"src/A.cs"}"""),
            """{"type":"tool_result","tool_use_id":"call-a","content":[{"type":"text","text":"one"},{"type":"text","text":"two"}]}""");

        var regions = Find(body);

        Assert.Equal(2, regions.Length);
        Assert.All(regions, region => Assert.Null(region.FilePath));
    }

    [Fact]
    public void A_result_in_one_text_block_carries_the_path_of_its_call()
    {
        var body = AnthropicBody(
            AnthropicCall("call-a", """{"file_path":"src/A.cs"}"""),
            """{"type":"tool_result","tool_use_id":"call-a","content":[{"type":"text","text":"one"}]}""");

        Assert.Equal("src/A.cs", Assert.Single(Find(body)).FilePath);
    }

    [Fact]
    public void A_call_that_is_not_a_read_carries_no_path()
    {
        var body = AnthropicBody(
            """{"type":"tool_use","id":"call-a","name":"Delete","input":{"file_path":"src/A.cs"}}""",
            AnthropicResult("call-a", "deleted"));

        Assert.Null(Assert.Single(Find(body)).FilePath);
    }
}
