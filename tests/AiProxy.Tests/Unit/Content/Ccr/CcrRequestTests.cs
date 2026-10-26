using System.Text;
using System.Text.Json;
using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>What the proxy reads from a Messages request before it decides on CCR, and the tool it appends.</summary>
public sealed class CcrRequestTests
{
    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void A_thread_continue_names_its_previous_message()
    {
        var request = CcrRequest.Read(Bytes("""{"model":"m","stream":true,"messages":[],"thread":{"type":"continue","previous_message_id":"msg_1"}}"""));

        Assert.NotNull(request);
        Assert.Equal(CcrResponse.Streamed, request.Response);
        Assert.Equal(CcrTools.None, request.Tools);
        Assert.Equal(CcrThread.Continue, request.Thread);
        Assert.Equal("msg_1", request.PreviousMessageId);
    }

    [Fact]
    public void A_thread_create_with_tools_is_read_as_one()
    {
        var request = CcrRequest.Read(Bytes("""{"stream":true,"tools":[{"name":"Read"}],"messages":[],"thread":{"type":"create"}}"""));

        Assert.NotNull(request);
        Assert.Equal(CcrTools.Defined, request.Tools);
        Assert.Equal(CcrThread.Create, request.Thread);
        Assert.Null(request.PreviousMessageId);
    }

    [Fact]
    public void A_request_without_a_thread_is_stateless_and_an_empty_tools_list_is_no_tools()
    {
        var request = CcrRequest.Read(Bytes("""{"tools":[],"messages":[]}"""));

        Assert.NotNull(request);
        Assert.Equal(CcrResponse.Whole, request.Response);
        Assert.Equal(CcrTools.None, request.Tools);
        Assert.Equal(CcrThread.None, request.Thread);
    }

    [Fact]
    public void A_body_that_is_not_a_json_object_is_not_a_request()
    {
        Assert.Null(CcrRequest.Read(Bytes("not json")));
        Assert.Null(CcrRequest.Read(Bytes("[1]")));
    }

    [Fact]
    public void The_tool_is_appended_last_and_every_other_byte_stays()
    {
        const string body = """{"tools":[{"name":"Read","description":"café <b>"} , {"name":"Bash"} ],"messages":[{"role":"user","content":"tools ]"}]}""";

        var appended = Encoding.UTF8.GetString(CcrRequest.AppendTool(Bytes(body))!);

        const string expected = $$"""{"tools":[{"name":"Read","description":"café <b>"} , {"name":"Bash"} ,{{CcrRetrieval.Definition}}],"messages":[{"role":"user","content":"tools ]"}]}""";
        Assert.Equal(expected, appended);
        using var parsed = JsonDocument.Parse(appended);
        Assert.Equal("proxy_retrieve", parsed.RootElement.GetProperty("tools")[2].GetProperty("name").GetString());
    }

    [Fact]
    public void A_request_without_tools_gets_none_appended()
    {
        Assert.Null(CcrRequest.AppendTool(Bytes("""{"messages":[]}""")));
        Assert.Null(CcrRequest.AppendTool(Bytes("""{"tools":[],"messages":[]}""")));
    }

    [Fact]
    public void A_request_that_already_carries_the_tool_is_left_alone()
    {
        var body = Bytes($$"""{"tools":[{{CcrRetrieval.Definition}}]}""");

        Assert.Null(CcrRequest.AppendTool(body));
    }

    [Fact]
    public void The_previous_message_is_rewritten_in_the_thread_and_the_diagnostics()
    {
        const string body = """{"messages":[],"thread":{"type":"continue","previous_message_id":"msg_1"},"diagnostics":{"previous_message_id":"msg_1"},"stream":true}""";

        var rewritten = Encoding.UTF8.GetString(CcrRequest.ContinueFrom(Bytes(body), "msg_9"));

        Assert.Equal("""{"messages":[],"thread":{"type":"continue","previous_message_id":"msg_9"},"diagnostics":{"previous_message_id":"msg_9"},"stream":true}""", rewritten);
    }
}
