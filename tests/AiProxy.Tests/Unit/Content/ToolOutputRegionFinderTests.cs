using System.Text;
using AiProxy.Core.Content;

namespace AiProxy.Tests.Unit.Content;

/// <summary>
/// The region finder, stated as behaviour: it finds the tool output in a request body and
/// nothing else.
/// </summary>
public class ToolOutputRegionFinderTests
{
    private static readonly ToolOutputRegionFinder Finder = new();

    private static ContentRegion[] Find(string body) => Finder.Find(Encoding.UTF8.GetBytes(body)).ToArray();

    [Fact]
    public void The_tool_result_of_an_anthropic_message_is_a_region()
    {
        var regions = Find("""{"messages":[{"role":"user","content":[{"type":"tool_result","content":"the tool said this"}]}]}""");

        Assert.Single(regions);
        Assert.Equal("the tool said this", regions[0].Text);
    }

    [Fact]
    public void The_tool_message_of_an_openai_request_is_a_region()
    {
        var regions = Find("""{"messages":[{"role":"tool","content":"the tool said this"}]}""");

        Assert.Single(regions);
        Assert.Equal("the tool said this", regions[0].Text);
    }

    [Fact]
    public void The_function_call_output_of_an_openai_response_is_a_region()
    {
        var regions = Find("""{"output":[{"type":"function_call_output","output":"the tool said this"}]}""");

        Assert.Single(regions);
        Assert.Equal("the tool said this", regions[0].Text);
    }

    [Fact]
    public void The_function_response_of_a_gemini_request_is_a_region()
    {
        var regions = Find("""{"contents":[{"parts":[{"functionResponse":{"response":{"output":"the tool said this"}}}]}]}""");

        Assert.Single(regions);
        Assert.Equal("the tool said this", regions[0].Text);
    }

    [Fact]
    public void The_same_text_in_a_user_message_is_not_a_region()
    {
        var body = """{"messages":[{"role":"user","content":"the tool said this"},{"role":"tool","content":"the tool said this"}]}""";

        var regions = Find(body);

        Assert.Single(regions);
    }

    [Fact]
    public void A_body_that_is_not_json_has_no_regions()
    {
        Assert.Empty(Find("not json at all"));
    }

    [Fact]
    public void A_json_body_without_tool_output_has_no_regions()
    {
        Assert.Empty(Find("""{"messages":[{"role":"user","content":"hello"}]}"""));
    }

    [Fact]
    public void The_region_covers_exactly_the_tool_output_in_the_body()
    {
        var body = """{"messages":[{"role":"tool","content":"the tool said this"}]}""";

        var region = Assert.Single(Find(body));

        Assert.Equal("\"the tool said this\"", body.Substring(region.Start, region.Length));
    }

    [Fact]
    public void A_body_the_finder_cannot_read_has_no_regions()
    {
        // A body nested deeper than the parser reads is a body the finder leaves alone,
        // rather than a body it reads halfway and guesses about.
        var body = new string('[', 100) + "\"the tool said this\"" + new string(']', 100);

        Assert.Empty(Find(body));
    }

    [Fact]
    public void The_regions_come_in_the_order_they_appear_in_the_body()
    {
        var body = """{"messages":[{"role":"tool","content":"first"},{"role":"user","content":"x"},{"role":"tool","content":"second"}]}""";

        var regions = Find(body);

        Assert.Equal(2, regions.Length);
        Assert.Equal("first", regions[0].Text);
        Assert.Equal("second", regions[1].Text);
    }
}
