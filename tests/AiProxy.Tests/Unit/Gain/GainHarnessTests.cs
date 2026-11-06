using AiProxy.Core.Gain;

namespace AiProxy.Tests.Unit.Gain;

public sealed class GainHarnessTests
{
    [Theory]
    [InlineData("anthropic", "claude")]
    [InlineData("openai", "codex")]
    [InlineData("gemini", "other")]
    [InlineData("unknown", "other")]
    [InlineData(null, "other")]
    public void A_provider_belongs_to_one_harness(string? provider, string harness)
    {
        Assert.Equal(harness, GainHarness.Of(provider));
    }

    [Fact]
    public void Harness_totals_merge_providers_and_keep_display_order()
    {
        var rows = new[]
        {
            new GainProviderTotals("gemini", new GainTotals(Requests: 1)),
            new GainProviderTotals(null, new GainTotals(Requests: 2)),
            new GainProviderTotals("openai", new GainTotals(Requests: 4)),
            new GainProviderTotals("anthropic", new GainTotals(Requests: 8))
        };

        var harnesses = GainReportBuilder.Harnesses(rows);

        Assert.Equal(["claude", "codex", "other"], harnesses.Select(static row => row.Name));
        Assert.Equal([8L, 4L, 3L], harnesses.Select(static row => row.Totals.Requests));
    }
}
