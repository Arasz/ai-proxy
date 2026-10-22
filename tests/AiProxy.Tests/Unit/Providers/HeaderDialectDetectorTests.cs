using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Providers;

/// <summary>The header signal on its own: the last resort.</summary>
public class HeaderDialectDetectorTests
{
    private readonly HeaderDialectDetector _detector = new();

    [Fact]
    public void The_anthropic_version_header_resolves_to_anthropic()
    {
        var detection = _detector.Detect(new DetectionRequest("/anything", Headers(("anthropic-version", "2023-06-01")), null));

        Assert.NotNull(detection);
        Assert.Equal(Dialect.Anthropic, detection.Dialect);
        Assert.Equal(DialectSource.Header, detection.Source);
    }

    [Fact]
    public void The_goog_api_key_header_resolves_to_gemini()
    {
        var detection = _detector.Detect(new DetectionRequest("/anything", Headers(("x-goog-api-key", "key")), null));

        Assert.NotNull(detection);
        Assert.Equal(Dialect.Gemini, detection.Dialect);
    }

    [Fact]
    public void A_user_agent_that_names_anthropic_resolves_to_anthropic()
    {
        var detection = _detector.Detect(new DetectionRequest("/anything", Headers(("user-agent", "claude-cli/1.0 (anthropic)")), null));

        Assert.NotNull(detection);
        Assert.Equal(Dialect.Anthropic, detection.Dialect);
    }

    [Fact]
    public void Headers_that_name_no_provider_resolve_to_nothing()
    {
        var detection = _detector.Detect(new DetectionRequest("/anything", Headers(("accept", "application/json")), null));

        Assert.Null(detection);
    }

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(header => header.Name, header => header.Value, StringComparer.OrdinalIgnoreCase);
}
