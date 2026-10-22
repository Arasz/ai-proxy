using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Providers;

/// <summary>
/// The detector's contract, stated as behaviour rather than as a description of the code:
/// which provider does this request belong to?
/// </summary>
public class CompositeDialectDetectorTests
{
    private static readonly CompositeDialectDetector Detector = new(
    [
        new PathDialectDetector(),
        new BodyDialectDetector(),
        new HeaderDialectDetector(),
    ]);

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
        var detection = Detector.Detect(new DetectionRequest(path, Headers(), null));

        Assert.Equal(expected, detection.Dialect);
    }

    [Fact]
    public void A_generic_path_with_an_anthropic_body_resolves_to_anthropic()
    {
        var body = """{"model":"claude-haiku-4-5","max_tokens":5,"messages":[]}""";

        var detection = Detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.Equal(Dialect.Anthropic, detection.Dialect);
    }

    [Fact]
    public void A_generic_path_with_an_openai_body_resolves_to_openai()
    {
        var body = """{"model":"gpt-4o","max_completion_tokens":5,"messages":[]}""";

        var detection = Detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.Equal(Dialect.OpenAi, detection.Dialect);
    }

    [Fact]
    public void A_generic_path_with_a_gemini_body_resolves_to_gemini()
    {
        var body = """{"contents":[{"parts":[{"text":"hi"}]}]}""";

        var detection = Detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.Equal(Dialect.Gemini, detection.Dialect);
    }

    [Fact]
    public void The_anthropic_version_header_resolves_a_path_that_names_nothing()
    {
        var detection = Detector.Detect(new DetectionRequest("/anything", Headers(("anthropic-version", "2023-06-01")), null));

        Assert.Equal(Dialect.Anthropic, detection.Dialect);
    }

    [Fact]
    public void The_goog_api_key_header_resolves_a_path_that_names_nothing()
    {
        var detection = Detector.Detect(new DetectionRequest("/anything", Headers(("x-goog-api-key", "key")), null));

        Assert.Equal(Dialect.Gemini, detection.Dialect);
    }

    [Fact]
    public void A_path_and_a_body_that_disagree_resolve_to_nothing_rather_than_a_guess()
    {
        // The caller's credential has unknown provenance when the path and the body
        // name different providers, so the request must not be routed to either.
        var body = """{"model":"gpt-4o","max_completion_tokens":5,"messages":[]}""";

        var detection = Detector.Detect(new DetectionRequest("/v1/messages", Headers(), body));

        Assert.Equal(Dialect.Unknown, detection.Dialect);
        Assert.True(detection.IsConflicted);
    }

    [Fact]
    public void A_request_with_no_signal_resolves_to_nothing()
    {
        var detection = Detector.Detect(new DetectionRequest("/whatever", Headers(), """{"foo":1}"""));

        Assert.Equal(Dialect.Unknown, detection.Dialect);
    }

    [Fact]
    public void A_body_that_is_not_json_resolves_to_nothing_rather_than_throwing()
    {
        var detection = Detector.Detect(new DetectionRequest("/whatever", Headers(), "not json at all"));

        Assert.Equal(Dialect.Unknown, detection.Dialect);
    }

    [Fact]
    public void Tools_carrying_cache_control_mark_a_body_as_anthropic()
    {
        var body = """{"model":"claude-haiku-4-5","messages":[],"tools":[{"name":"t","cache_control":{"type":"ephemeral"}}]}""";

        var detection = Detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.Equal(Dialect.Anthropic, detection.Dialect);
    }

    [Fact]
    public void A_path_named_provider_reports_the_path_as_its_source()
    {
        var detection = Detector.Detect(new DetectionRequest("/v1/messages", Headers(), null));

        Assert.Equal(DialectSource.Path, detection.Source);
        Assert.True(detection.CredentialBelongsToCaller);
    }

    [Fact]
    public void A_body_detected_provider_reports_the_body_as_its_source()
    {
        var body = """{"model":"claude-haiku-4-5","max_tokens":5,"messages":[]}""";

        var detection = Detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.Equal(DialectSource.Body, detection.Source);
    }

    [Fact]
    public void A_body_detected_providers_credential_does_not_belong_to_the_caller()
    {
        // The record's F4 rule: a body-detected provider may not receive the caller's
        // credential, because the caller addressed no provider endpoint and the
        // credential's provenance is unknown.
        var body = """{"model":"claude-haiku-4-5","max_tokens":5,"messages":[]}""";

        var detection = Detector.Detect(new DetectionRequest("/gateway/invoke", Headers(), body));

        Assert.False(detection.CredentialBelongsToCaller);
    }

    [Fact]
    public void A_header_detected_provider_does_not_treat_the_credential_as_the_callers()
    {
        // The headers are the last resort, and a header names a provider without the
        // caller having addressed that provider's endpoint, so the credential's
        // provenance is no better established than a body's.
        var detection = Detector.Detect(new DetectionRequest("/anything", Headers(("anthropic-version", "2023-06-01")), null));

        Assert.Equal(DialectSource.Header, detection.Source);
        Assert.False(detection.CredentialBelongsToCaller);
    }

    [Fact]
    public void A_conflict_names_no_source()
    {
        var body = """{"model":"gpt-4o","max_completion_tokens":5,"messages":[]}""";

        var detection = Detector.Detect(new DetectionRequest("/v1/messages", Headers(), body));

        Assert.Equal(Dialect.Unknown, detection.Dialect);
        Assert.Equal(DialectSource.None, detection.Source);
        Assert.False(detection.CredentialBelongsToCaller);
    }

    [Fact]
    public void A_request_with_no_signal_names_no_source()
    {
        var detection = Detector.Detect(new DetectionRequest("/whatever", Headers(), """{"foo":1}"""));

        Assert.Equal(DialectSource.None, detection.Source);
        Assert.False(detection.CredentialBelongsToCaller);
    }

    [Fact]
    public void A_path_that_names_a_provider_wins_over_a_body_that_names_the_same_one()
    {
        var body = """{"model":"claude-haiku-4-5","max_tokens":5,"messages":[]}""";

        var detection = Detector.Detect(new DetectionRequest("/v1/messages", Headers(), body));

        Assert.Equal(Dialect.Anthropic, detection.Dialect);
        Assert.Equal(DialectSource.Path, detection.Source);
        Assert.Equal(DetectionOutcome.Resolved, detection.Outcome);
    }

    [Fact]
    public void A_header_that_disagrees_with_the_body_does_not_conflict()
    {
        // A header is a hint about the client, not a statement about the body, so it
        // never turns a disagreement into a refusal.
        var body = """{"model":"gpt-4o","max_completion_tokens":5,"messages":[]}""";

        var detection = Detector.Detect(
            new DetectionRequest("/gateway/invoke", Headers(("anthropic-version", "2023-06-01")), body));

        Assert.Equal(Dialect.OpenAi, detection.Dialect);
        Assert.Equal(DialectSource.Body, detection.Source);
        Assert.False(detection.IsConflicted);
    }

    [Fact]
    public void A_header_that_agrees_with_the_body_says_so_in_the_reason()
    {
        var body = """{"model":"claude-haiku-4-5","max_tokens":5,"messages":[]}""";

        var detection = Detector.Detect(
            new DetectionRequest("/gateway/invoke", Headers(("anthropic-version", "2023-06-01")), body));

        Assert.Equal(Dialect.Anthropic, detection.Dialect);
        Assert.Contains("headers agree", detection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_with_no_signal_is_unresolved_rather_than_conflicted()
    {
        var detection = Detector.Detect(new DetectionRequest("/whatever", Headers(), """{"foo":1}"""));

        Assert.Equal(Dialect.Unknown, detection.Dialect);
        Assert.Equal(DetectionOutcome.Unresolved, detection.Outcome);
        Assert.False(detection.IsConflicted);
    }
}
