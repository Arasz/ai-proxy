using AiProxy.Core.Gain;

namespace AiProxy.Tests.Unit.Gain;

public sealed class GainHarnessTests
{
    [Theory]
    [InlineData("anthropic", null, "claude")]
    [InlineData("anthropic", "api.anthropic.com", "claude")]
    [InlineData("openai", "chatgpt.com", "codex")]
    [InlineData("openai", "openrouter.ai", "openrouter")]
    [InlineData("openai", "OpenRouter.ai", "openrouter")]
    [InlineData("openai", "api.openai.com", "openai")]
    [InlineData("openai", "eu.openrouter.ai", "openrouter")]
    [InlineData("openai", "us.api.openai.com", "openai")]
    [InlineData("openai", null, "openai (host unknown)")]
    [InlineData("openai", "example.test", "other")]
    [InlineData("gemini", "chatgpt.com", "other")]
    [InlineData("unknown", null, "other")]
    [InlineData(null, null, "other")]
    public void A_provider_and_host_belong_to_one_harness(string? provider, string? host, string harness)
    {
        Assert.Equal(harness, GainHarness.Of(provider, host));
    }

    [Fact]
    public void Harness_totals_merge_providers_and_keep_display_order()
    {
        var rows = new[]
        {
            new GainProviderTotals("gemini", new GainTotals(Requests: 1)),
            new GainProviderTotals(null, new GainTotals(Requests: 2)),
            new GainProviderTotals("openai", new GainTotals(Requests: 4), "chatgpt.com"),
            new GainProviderTotals("anthropic", new GainTotals(Requests: 8))
        };

        var harnesses = GainReportBuilder.Harnesses(rows);

        Assert.Equal(["claude", "codex", "other"], harnesses.Select(static row => row.Name));
        Assert.Equal([8L, 4L, 3L], harnesses.Select(static row => row.Totals.Requests));
    }

    [Fact]
    public void Old_rows_without_a_host_and_new_rows_with_one_get_their_own_harness_rows()
    {
        var rows = new[]
        {
            new GainProviderTotals("openai", new GainTotals(Requests: 5)),
            new GainProviderTotals("openai", new GainTotals(Requests: 2), "openrouter.ai"),
            new GainProviderTotals("openai", new GainTotals(Requests: 3), "chatgpt.com")
        };

        var harnesses = GainReportBuilder.Harnesses(rows);

        Assert.Equal(["codex", "openrouter", "openai (host unknown)"], harnesses.Select(static row => row.Name));
        Assert.Equal([3L, 2L, 5L], harnesses.Select(static row => row.Totals.Requests));
    }
}
