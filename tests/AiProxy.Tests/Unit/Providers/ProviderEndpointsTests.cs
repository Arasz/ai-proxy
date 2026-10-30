using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Providers;

public sealed class ProviderEndpointsTests
{
    [Theory]
    [InlineData(Dialect.Anthropic, "POST", "/v1/messages")]
    [InlineData(Dialect.Anthropic, "post", "/v1/messages/")]
    [InlineData(Dialect.OpenAi, "POST", "/v1/chat/completions")]
    [InlineData(Dialect.OpenAi, "POST", "/v1/responses")]
    [InlineData(Dialect.OpenAi, "POST", "/v1/completions")]
    [InlineData(Dialect.OpenAi, "POST", "/api/v1/chat/completions")]
    [InlineData(Dialect.OpenAi, "POST", "/api/v1/messages")]
    [InlineData(Dialect.Gemini, "POST", "/v1beta/models/gemini-2.5-pro:generateContent")]
    [InlineData(Dialect.Gemini, "POST", "/v1beta/models/gemini-2.5-pro:streamGenerateContent")]
    public void A_generation_endpoint_is_a_model_call(Dialect dialect, string method, string path)
    {
        Assert.True(ProviderEndpoints.IsModelCall(dialect, method, path));
    }

    [Theory]
    [InlineData(Dialect.Anthropic, "POST", "/api/event_logging/v2/batch")]
    [InlineData(Dialect.Anthropic, "POST", "/ingest/i/v0/e/")]
    [InlineData(Dialect.Anthropic, "GET", "/v1/messages")]
    [InlineData(Dialect.Anthropic, "POST", "/v1/messages/batches")]
    [InlineData(Dialect.Anthropic, "POST", "/v1/messages/count_tokens")]
    [InlineData(Dialect.Anthropic, "POST", "/v1/chat/completions")]
    [InlineData(Dialect.OpenAi, "POST", "/v1/embeddings")]
    [InlineData(Dialect.OpenAi, "POST", "/v1/files")]
    [InlineData(Dialect.Gemini, "POST", "/v1beta/models/gemini-2.5-pro:countTokens")]
    [InlineData(Dialect.Unknown, "POST", "/v1/messages")]
    public void Telemetry_and_every_other_call_is_not_a_model_call(Dialect dialect, string method, string path)
    {
        Assert.False(ProviderEndpoints.IsModelCall(dialect, method, path));
    }
}
