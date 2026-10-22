using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Providers;

/// <summary>The path signal on its own: the strongest statement a request makes about its provider.</summary>
public class PathDialectDetectorTests
{
    private readonly PathDialectDetector _detector = new();

    [Theory]
    [InlineData("/v1/messages", Dialect.Anthropic)]
    [InlineData("/v1/messages/count_tokens", Dialect.Anthropic)]
    [InlineData("/v1/chat/completions", Dialect.OpenAi)]
    [InlineData("/v1/responses", Dialect.OpenAi)]
    [InlineData("/v1/models", Dialect.OpenAi)]
    [InlineData("/v1beta/models/gemini-2.5-flash:generateContent", Dialect.Gemini)]
    public void A_path_that_names_a_provider_resolves_to_that_provider(string path, Dialect expected)
    {
        var detection = _detector.Detect(new DetectionRequest(path, Headers(), null));

        Assert.NotNull(detection);
        Assert.Equal(expected, detection.Dialect);
        Assert.Equal(DialectSource.Path, detection.Source);
    }

    [Theory]
    [InlineData("/gateway/invoke")]
    [InlineData("/mcp")]
    [InlineData("/")]
    public void A_path_that_names_no_provider_resolves_to_nothing(string path)
    {
        var detection = _detector.Detect(new DetectionRequest(path, Headers(), null));

        Assert.Null(detection);
    }

    [Theory]
    [InlineData("/v1/messages-extra")]
    [InlineData("/v1/messagesx")]
    public void A_path_that_only_looks_like_a_provider_endpoint_resolves_to_nothing(string path)
    {
        // The rule matches whole path segments, so a path that merely starts with the
        // same letters is not the provider's endpoint.
        var detection = _detector.Detect(new DetectionRequest(path, Headers(), null));

        Assert.Null(detection);
    }

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(header => header.Name, header => header.Value, StringComparer.OrdinalIgnoreCase);
}
