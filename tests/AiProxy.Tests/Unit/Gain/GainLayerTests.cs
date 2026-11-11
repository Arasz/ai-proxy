using AiProxy.Core.Gain;

namespace AiProxy.Tests.Unit.Gain;

public sealed class GainLayerTests
{
    [Theory]
    [InlineData(GainLabels.Rules.Unchanged, GainLabels.Layers.None)]
    [InlineData(GainLabels.Rules.CcrPlanFailed, GainLabels.Layers.None)]
    [InlineData(GainLabels.Rules.CcrTool, GainLabels.Layers.CcrTool)]
    [InlineData("json.minify", GainLabels.Layers.ToolResult)]
    public void A_request_rule_maps_to_a_known_layer(string rule, string layer)
    {
        Assert.Equal(layer, GainLabels.Layers.Of(rule));
    }
}
