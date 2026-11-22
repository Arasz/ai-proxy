using AiProxy.Ccr;
using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>The parser answers with a request or a refusal, never a half-filled result.</summary>
public sealed class CcrRetrievalParserTests
{
    private static readonly CcrRetrievalParser Parser = new(new RetrievalArgumentSchema());

    [Fact]
    public void A_valid_call_parses_to_a_request()
    {
        var result = Parser.Parse("""{"id":"0123456789abcdef","mode":"whole"}""", CcrOptions.Default);

        var request = Assert.IsType<CcrRetrievalRequest>(result.Value);
        Assert.Equal("0123456789abcdef", request.Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"id":"0123456789abcdef","mode":"lines"}""")]
    [InlineData("""{"id":"0123456789abcdef","mode":"pattern"}""")]
    public void Malformed_arguments_parse_to_a_refusal_with_a_reason(string arguments)
    {
        var result = Parser.Parse(arguments, CcrOptions.Default);

        var refusal = Assert.IsType<CcrRefusal>(result.Value);
        Assert.NotEmpty(refusal.Reason);
    }
}
