using AiProxy.Core.Providers;

namespace AiProxy.Core.Routing;

/// <summary>Decides where a request goes, in the order the rules are meant to be read.</summary>
/// <remarks>
/// A path that names a provider is the strongest statement a request makes, and it goes
/// to that provider. A path an operator named for another server is the next strongest,
/// and it is carried unchanged without the body ever being read. Only a generic path
/// falls through to the shape of the body, and a request that names no provider at all
/// is refused.
/// </remarks>
public sealed class CompositeRequestRouter : IRequestRouter
{
    private readonly IRequestRouter _providerRouter;
    private readonly IRequestRouter _passThroughRouter;

    /// <summary>Holds the provider router and the pass-through router.</summary>
    public CompositeRequestRouter(IRequestRouter providerRouter, IRequestRouter passThroughRouter)
    {
        ArgumentNullException.ThrowIfNull(providerRouter);
        ArgumentNullException.ThrowIfNull(passThroughRouter);

        _providerRouter = providerRouter;
        _passThroughRouter = passThroughRouter;
    }

    /// <summary>Routes the request, or refuses it.</summary>
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

        if (detection.Source == DialectSource.Path)
        {
            return _providerRouter.Route(request);
        }

        var passThrough = _passThroughRouter.Route(request);
        if (passThrough is RouteDecision.PassThroughRoute)
        {
            return passThrough;
        }

        if (detection.Dialect != Dialect.Unknown)
        {
            return _providerRouter.Route(request);
        }

        return passThrough;
    }
}
