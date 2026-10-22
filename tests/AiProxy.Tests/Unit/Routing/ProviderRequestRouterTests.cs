using AiProxy.Core.Providers;
using AiProxy.Core.Routing;

namespace AiProxy.Tests.Unit.Routing;

/// <summary>
/// Provider routing does not replace or require credentials configured by the proxy.
/// </summary>
public class ProviderRequestRouterTests
{
    private static readonly Uri AnthropicUpstream = new("https://api.anthropic.com");
    private static readonly Uri OpenAiUpstream = new("https://api.openai.com");

    [Fact]
    public void A_path_named_provider_is_routed_without_a_proxy_credential()
    {
        var router = Router();

        var route = router.Route(Request(
            "/v1/messages",
            Detection.Resolved(Dialect.Anthropic, DialectSource.Path, "path /v1/messages is a Anthropic endpoint")));

        var provider = Assert.IsType<RouteDecision.ProviderRoute>(route);
        Assert.Equal(Dialect.Anthropic, provider.Dialect);
        Assert.Equal(AnthropicUpstream, provider.Upstream);
    }

    [Fact]
    public void A_body_detected_provider_is_routed_without_a_proxy_credential()
    {
        var router = Router();

        var route = router.Route(Request(
            "/gateway/invoke",
            Detection.Resolved(Dialect.Anthropic, DialectSource.Body, "body shape is Anthropic")));

        var provider = Assert.IsType<RouteDecision.ProviderRoute>(route);
    }

    [Fact]
    public void A_header_detected_provider_is_routed_without_a_proxy_credential()
    {
        var router = Router();

        var route = router.Route(Request(
            "/gateway/invoke",
            Detection.Resolved(Dialect.Anthropic, DialectSource.Header, "headers say Anthropic")));

        var provider = Assert.IsType<RouteDecision.ProviderRoute>(route);
    }

    [Fact]
    public void A_body_detected_provider_does_not_require_a_proxy_credential()
    {
        var router = Router();

        var route = router.Route(Request(
            "/gateway/invoke",
            Detection.Resolved(Dialect.Anthropic, DialectSource.Body, "body shape is Anthropic")));

        Assert.IsType<RouteDecision.ProviderRoute>(route);
    }

    [Fact]
    public void A_conflicted_detection_is_refused_before_an_upstream_is_chosen()
    {
        var router = Router();

        var route = router.Route(Request(
            "/v1/messages",
            Detection.Conflicted("path says Anthropic but body says OpenAi; refusing to guess")));

        var refusal = Assert.IsType<RouteDecision.RefusedRoute>(route);
        Assert.Equal(RefusalReason.CredentialAmbiguous, refusal.Refusal);
        Assert.Contains("provenance", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_with_no_configured_upstream_is_refused()
    {
        var router = new ProviderRequestRouter(new UpstreamCatalog(new Dictionary<Dialect, Uri>()));

        var route = router.Route(Request(
            "/v1/messages",
            Detection.Resolved(Dialect.Anthropic, DialectSource.Path, "path /v1/messages is a Anthropic endpoint")));

        var refusal = Assert.IsType<RouteDecision.RefusedRoute>(route);
        Assert.Equal(RefusalReason.Unknown, refusal.Refusal);
        Assert.Equal("routing_unknown", refusal.Code);
        Assert.Contains("no upstream", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_that_names_no_provider_is_refused()
    {
        var router = Router();

        var route = router.Route(Request(
            "/whatever",
            Detection.Unresolved("no dialect signal in path /whatever, headers or body")));

        var refusal = Assert.IsType<RouteDecision.RefusedRoute>(route);
        Assert.Equal(RefusalReason.Unknown, refusal.Refusal);
        Assert.Equal("routing_unknown", refusal.Code);
    }

    private static ProviderRequestRouter Router() =>
        new(
            new UpstreamCatalog(new Dictionary<Dialect, Uri>
            {
                [Dialect.Anthropic] = AnthropicUpstream,
                [Dialect.OpenAi] = OpenAiUpstream,
            }));

    private static RoutingRequest Request(string path, Detection detection) =>
        new(path, new Dictionary<string, string>(), null, detection);
}
