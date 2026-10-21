using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Providers;

/// <summary>
/// The detector's contract, stated as behaviour rather than as a description of the code:
/// which provider does this request belong to?
/// </summary>
public class DialectDetectorTests
{
    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(header => header.Name, header => header.Value, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("/v1/messages", Dialect.Anthropic)]
    [InlineData("/v1/messages/count_tokens", Dialect.Anthropic)]
    [InlineData("/v1/chat/completions", Dialect.OpenAi)]
    [InlineData("/v1/responses", Dialect.OpenAi)]
    [InlineData("/v1/models", Dialect.OpenAi)]
    [InlineData("/v1beta/models/gemini-2.5-flash:generateContent", Dialect.Gemini)]
    public void A_path_that_names_a_provider_decides_it(string path, Dialect expected)
    {
        var detection = DialectDetector.Detect(path, Headers(), null);

        Assert.Equal(expected, detection.Dialect);
    }

    [Fact]
    public void A_generic_path_with_an_anthropic_body_resolves_to_anthropic()
    {
        var body = """{"model":"claude-haiku-4-5","max_tokens":5,"messages":[]}""";

        var detection = DialectDetector.Detect("/gateway/invoke", Headers(), body);

        Assert.Equal(Dialect.Anthropic, detection.Dialect);
    }

    [Fact]
    public void A_generic_path_with_an_openai_body_resolves_to_openai()
    {
        var body = """{"model":"gpt-4o","max_completion_tokens":5,"messages":[]}""";

        var detection = DialectDetector.Detect("/gateway/invoke", Headers(), body);

        Assert.Equal(Dialect.OpenAi, detection.Dialect);
    }

    [Fact]
    public void A_generic_path_with_a_gemini_body_resolves_to_gemini()
    {
        var body = """{"contents":[{"parts":[{"text":"hi"}]}]}""";

        var detection = DialectDetector.Detect("/gateway/invoke", Headers(), body);

        Assert.Equal(Dialect.Gemini, detection.Dialect);
    }

    [Fact]
    public void The_anthropic_version_header_resolves_a_path_that_names_nothing()
    {
        var detection = DialectDetector.Detect("/anything", Headers(("anthropic-version", "2023-06-01")), null);

        Assert.Equal(Dialect.Anthropic, detection.Dialect);
    }

    [Fact]
    public void The_goog_api_key_header_resolves_a_path_that_names_nothing()
    {
        var detection = DialectDetector.Detect("/anything", Headers(("x-goog-api-key", "key")), null);

        Assert.Equal(Dialect.Gemini, detection.Dialect);
    }

    [Fact]
    public void A_path_and_a_body_that_disagree_resolve_to_nothing_rather_than_a_guess()
    {
        // The caller's credential has unknown provenance when the path and the body
        // name different providers, so the request must not be routed to either.
        var body = """{"model":"gpt-4o","max_completion_tokens":5,"messages":[]}""";

        var detection = DialectDetector.Detect("/v1/messages", Headers(), body);

        Assert.Equal(Dialect.Unknown, detection.Dialect);
    }

    [Fact]
    public void A_request_with_no_signal_resolves_to_nothing()
    {
        var detection = DialectDetector.Detect("/whatever", Headers(), """{"foo":1}""");

        Assert.Equal(Dialect.Unknown, detection.Dialect);
    }

    [Fact]
    public void A_body_that_is_not_json_resolves_to_nothing_rather_than_throwing()
    {
        var detection = DialectDetector.Detect("/whatever", Headers(), "not json at all");

        Assert.Equal(Dialect.Unknown, detection.Dialect);
    }

    [Fact]
    public void Tools_carrying_cache_control_mark_a_body_as_anthropic()
    {
        var body = """{"model":"claude-haiku-4-5","messages":[],"tools":[{"name":"t","cache_control":{"type":"ephemeral"}}]}""";

        var detection = DialectDetector.Detect("/gateway/invoke", Headers(), body);

        Assert.Equal(Dialect.Anthropic, detection.Dialect);
    }
}
