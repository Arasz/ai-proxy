namespace AiProxy.Core.Routing;

/// <summary>Routes traffic that is not provider traffic, by the operator's path-prefix rules.</summary>
public sealed class PassThroughRequestRouter : IRequestRouter
{
    private readonly IPassThroughCatalog _catalog;

    /// <summary>Holds the operator's rules.</summary>
    public PassThroughRequestRouter(IPassThroughCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _catalog = catalog;
    }

    /// <summary>Carries the request to the upstream its path prefix names, or refuses it.</summary>
    public RouteDecision Route(RoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _catalog.TryGetUpstream(request.Path, out var upstream)
            ? new RouteDecision.PassThroughRoute(upstream, $"path {request.Path} is covered by an operator's pass-through rule")
            : new RouteDecision.RefusedRoute(
                RefusalReason.Unknown,
                $"no provider was named for path {request.Path} and no pass-through rule covers it");
    }
}
