using AiProxy.Core.Providers;

namespace AiProxy.Core.Routing;

/// <summary>Routes a request that belongs to a provider, and applies the credential rule.</summary>
/// <remarks>
/// The rule is the one the research record measured. A path-named provider keeps the
/// caller's credential, because the caller addressed that provider's endpoint. A
/// body-detected or header-detected provider gets the proxy's own credential, because
/// the caller's credential may belong to the other provider, and a request with no
/// proxy credential of its own is refused rather than forwarded.
/// </remarks>
public sealed class ProviderRequestRouter : IRequestRouter
{
    private readonly UpstreamCatalog _upstreams;
    private readonly ICredentialProvider _credentials;

    /// <summary>Holds the configured upstreams and the proxy's own credentials.</summary>
    public ProviderRequestRouter(UpstreamCatalog upstreams, ICredentialProvider credentials)
    {
        ArgumentNullException.ThrowIfNull(upstreams);
        ArgumentNullException.ThrowIfNull(credentials);

        _upstreams = upstreams;
        _credentials = credentials;
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

        var credential = detection.CredentialBelongsToCaller
            ? CredentialSource.Caller
            : CredentialSource.Proxy;

        if (credential == CredentialSource.Proxy && !_credentials.TryGetCredential(detection.Dialect, out _))
        {
            return new RouteDecision.RefusedRoute(
                RefusalReason.CredentialAmbiguous,
                $"the request names {detection.Dialect} by its body or its headers, so the caller's credential does not belong there, and the proxy holds no credential of its own");
        }

        return new RouteDecision.ProviderRoute(detection.Dialect, upstream, credential, detection.Reason);
    }
}
