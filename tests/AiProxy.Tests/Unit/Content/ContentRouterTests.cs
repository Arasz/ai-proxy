using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content;

/// <summary>The router, stated as behaviour: the handler decides what it claims.</summary>
public class ContentRouterTests
{
    private static readonly ContentRouter Router = new(
        [
            new LogCompressionHandler(),
            new JsonCompressionHandler(),
        ],
        new PassThroughCompressionHandler());

    [Fact]
    public void The_handler_that_claims_the_kind_is_the_one_the_router_picks()
    {
        var route = Router.Route(new ContentPart("request", 1, ContentType.Log, "text"));

        Assert.IsType<LogCompressionHandler>(route.Handler);
    }

    [Fact]
    public void A_kind_nobody_claims_falls_to_the_fallback()
    {
        var route = Router.Route(new ContentPart("request", 1, ContentType.Xml, "text"));

        Assert.IsType<PassThroughCompressionHandler>(route.Handler);
    }

    [Fact]
    public void The_route_says_which_handler_it_chose()
    {
        var route = Router.Route(new ContentPart("request", 1, ContentType.Log, "text"));

        Assert.Contains("log", route.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_route_carries_the_kind_it_routed()
    {
        var route = Router.Route(new ContentPart("request", 1, ContentType.Json, "text"));

        Assert.Equal(ContentType.Json, route.Type);
    }
}
