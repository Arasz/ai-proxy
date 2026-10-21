using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Providers;

/// <summary>The body signal on its own: the shape the caller actually sent.</summary>
public class BodyDialectDetectorTests
{
    private readonly BodyDialectDetector _detector = new();

    [Fact]
    public void A_body_with_an_anthropic_shape_resolves_to_anthropic()
    {
        var body = """{"model":"claude-haiku-4-5","max_tokens":5,"messages":[]}""";

        var detection = _detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.NotNull(detection);
        Assert.Equal(Dialect.Anthropic, detection.Dialect);
        Assert.Equal(DialectSource.Body, detection.Source);
    }

    [Fact]
    public void A_body_with_an_openai_shape_resolves_to_openai()
    {
        var body = """{"model":"gpt-4o","max_completion_tokens":5,"messages":[]}""";

        var detection = _detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.NotNull(detection);
        Assert.Equal(Dialect.OpenAi, detection.Dialect);
    }

    [Fact]
    public void A_body_with_a_gemini_shape_resolves_to_gemini()
    {
        var body = """{"contents":[{"parts":[{"text":"hi"}]}]}""";

        var detection = _detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.NotNull(detection);
        Assert.Equal(Dialect.Gemini, detection.Dialect);
    }

    [Fact]
    public void Tools_carrying_cache_control_mark_a_body_as_anthropic()
    {
        var body = """{"model":"claude-haiku-4-5","messages":[],"tools":[{"name":"t","cache_control":{"type":"ephemeral"}}]}""";

        var detection = _detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.NotNull(detection);
        Assert.Equal(Dialect.Anthropic, detection.Dialect);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"foo":1}""")]
    [InlineData("""[1,2,3]""")]
    public void A_body_that_carries_no_dialect_shape_resolves_to_nothing(string body)
    {
        var detection = _detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.Null(detection);
    }

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(header => header.Name, header => header.Value, StringComparer.OrdinalIgnoreCase);
}
