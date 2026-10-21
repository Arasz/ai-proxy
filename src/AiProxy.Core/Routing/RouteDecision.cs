using AiProxy.Core.Providers;

namespace AiProxy.Core.Routing;

/// <summary>Where a request goes, or why it does not go anywhere.</summary>
public abstract record RouteDecision
{
    private RouteDecision()
    {
    }

    /// <summary>The request belongs to a provider, and goes to that provider's upstream.</summary>
    /// <param name="Dialect">The provider's dialect.</param>
    /// <param name="Upstream">The upstream the request goes to.</param>
    /// <param name="Credential">Whose credential travels with the request.</param>
    /// <param name="Reason">The evidence trail behind the decision.</param>
    public sealed record ProviderRoute(
        Dialect Dialect,
        Uri Upstream,
        CredentialSource Credential,
        string Reason) : RouteDecision;

    /// <summary>The request is not provider traffic, and is carried to a configured upstream unchanged.</summary>
    /// <param name="Upstream">The upstream the request goes to.</param>
    /// <param name="Reason">The evidence trail behind the decision.</param>
    public sealed record PassThroughRoute(Uri Upstream, string Reason) : RouteDecision;

    /// <summary>The request is refused, because relaying it would be worse than refusing it.</summary>
    /// <param name="Refusal">Why the request is refused.</param>
    /// <param name="Reason">What was not known, in words.</param>
    public sealed record RefusedRoute(RefusalReason Refusal, string Reason) : RouteDecision
    {
        /// <summary>The code that goes on the wire with the refusal.</summary>
        public string Code => RefusalCode.Of(Refusal);
    }
}
