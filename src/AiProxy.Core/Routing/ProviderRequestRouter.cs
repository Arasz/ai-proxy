using AiProxy.Core.Providers;

namespace AiProxy.Core.Routing;

/// <summary>Routes a request that belongs to a provider.</summary>
public sealed class ProviderRequestRouter : IRequestRouter
{
    private readonly UpstreamCatalog _upstreams;

    /// <summary>Holds the configured upstreams.</summary>
    public ProviderRequestRouter(UpstreamCatalog upstreams)
    {
        ArgumentNullException.ThrowIfNull(upstreams);

        _upstreams = upstreams;
    }

    /// <summary>Routes the request to its provider's upstream, or refuses it.</summary>
    public RouteDecision Route(RoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var detection = request.Detection;

        if (detection.IsConflicted)
        {
            return new RouteDecision.RefusedRoute(
                RefusalReason.CredentialAmbiguous,
                $"the path and the body name different providers, so the credential's provenance is unknown: {detection.Reason}");
        }

        if (detection.Dialect == Dialect.Unknown)
        {
            return new RouteDecision.RefusedRoute(
                RefusalReason.Unknown,
                $"no provider was named for path {request.Path}: {detection.Reason}");
        }

        if (!_upstreams.TryGetProviderUpstream(detection.Dialect, out var upstream))
        {
            return new RouteDecision.RefusedRoute(
                RefusalReason.Unknown,
                $"no upstream is configured for {detection.Dialect}");
        }

        return new RouteDecision.ProviderRoute(detection.Dialect, upstream, detection.Reason);
    }
}
