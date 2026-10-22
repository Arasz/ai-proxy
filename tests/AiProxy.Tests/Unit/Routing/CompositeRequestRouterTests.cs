using AiProxy.Core.Providers;
using AiProxy.Core.Routing;

namespace AiProxy.Tests.Unit.Routing;

/// <summary>The order the rules are read in: the path first, then the operator's prefix, then the body.</summary>
public class CompositeRequestRouterTests
{
    private static readonly Uri AnthropicUpstream = new("https://api.anthropic.com");
    private static readonly Uri McpUpstream = new("http://127.0.0.1:7801");

    [Fact]
    public void A_path_named_provider_goes_to_the_provider()
    {
        var router = Router();

        var route = router.Route(Request(
            "/v1/messages",
            Detection.Resolved(Dialect.Anthropic, DialectSource.Path, "path /v1/messages is a Anthropic endpoint")));

        var provider = Assert.IsType<RouteDecision.ProviderRoute>(route);
        Assert.Equal(AnthropicUpstream, provider.Upstream);
    }

    [Fact]
    public void A_generic_path_whose_body_names_a_provider_goes_to_the_provider()
    {
        var router = Router();

        var route = router.Route(Request(
            "/gateway/invoke",
            Detection.Resolved(Dialect.Anthropic, DialectSource.Body, "body shape is Anthropic")));

        var provider = Assert.IsType<RouteDecision.ProviderRoute>(route);
    }

    [Fact]
    public void A_path_under_an_operators_prefix_is_carried_even_when_the_body_names_a_provider()
    {
        // The operator named this path for another server, and a body shape is a weaker
        // statement than that. The traffic belongs to the MCP server, unchanged.
        var router = Router();

        var route = router.Route(Request(
            "/mcp",
            Detection.Resolved(Dialect.Gemini, DialectSource.Body, "body shape is Gemini")));

        var passThrough = Assert.IsType<RouteDecision.PassThroughRoute>(route);
        Assert.Equal(McpUpstream, passThrough.Upstream);
    }

    [Fact]
    public void A_request_that_names_no_provider_and_matches_no_prefix_is_refused()
    {
        var router = Router();

        var route = router.Route(Request(
            "/whatever",
            Detection.Unresolved("no dialect signal in path /whatever, headers or body")));

        var refusal = Assert.IsType<RouteDecision.RefusedRoute>(route);
        Assert.Equal(RefusalReason.Unknown, refusal.Refusal);
        Assert.Equal("routing_unknown", refusal.Code);
    }

    [Fact]
    public void A_conflicted_request_is_refused()
    {
        var router = Router();

        var route = router.Route(Request(
            "/v1/messages",
            Detection.Conflicted("path says Anthropic but body says OpenAi; refusing to guess")));

        var refusal = Assert.IsType<RouteDecision.RefusedRoute>(route);
        Assert.Equal(RefusalReason.CredentialAmbiguous, refusal.Refusal);
    }

    private static CompositeRequestRouter Router() =>
        new(
            new ProviderRequestRouter(
                new UpstreamCatalog(new Dictionary<Dialect, Uri> { [Dialect.Anthropic] = AnthropicUpstream })),
            new PassThroughRequestRouter(
                new PassThroughCatalog([new PassThroughUpstream("/mcp", McpUpstream)])));

    private static RoutingRequest Request(string path, Detection detection) =>
        new(path, new Dictionary<string, string>(), null, detection);
}
