using AiProxy.Core.Providers;
using AiProxy.Core.Routing;

namespace AiProxy.Tests.Unit.Routing;

/// <summary>The operator's path-prefix rules, which carry traffic that is not provider traffic.</summary>
public class PassThroughRequestRouterTests
{
    private static readonly Uri McpUpstream = new("http://127.0.0.1:7801");

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/messages")]
    public void A_path_under_a_configured_prefix_is_carried_to_that_upstream(string path)
    {
        var router = new PassThroughRequestRouter(new PassThroughCatalog([new PassThroughUpstream("/mcp", McpUpstream)]));

        var route = router.Route(Request(path));

        var passThrough = Assert.IsType<RouteDecision.PassThroughRoute>(route);
        Assert.Equal(McpUpstream, passThrough.Upstream);
    }

    [Theory]
    [InlineData("/mcpother")]
    [InlineData("/other")]
    public void A_path_outside_every_prefix_is_refused(string path)
    {
        var router = new PassThroughRequestRouter(new PassThroughCatalog([new PassThroughUpstream("/mcp", McpUpstream)]));

        var route = router.Route(Request(path));

        var refusal = Assert.IsType<RouteDecision.RefusedRoute>(route);
        Assert.Equal(RefusalReason.Unknown, refusal.Refusal);
    }

    private static RoutingRequest Request(string path) =>
        new(path, new Dictionary<string, string>(), null, Detection.Unresolved("no dialect signal"));
}
