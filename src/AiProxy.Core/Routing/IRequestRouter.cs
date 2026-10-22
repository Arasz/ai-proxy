namespace AiProxy.Core.Routing;

/// <summary>Answers where a request goes.</summary>
public interface IRequestRouter
{
    /// <summary>Routes a request, or refuses it.</summary>
    RouteDecision Route(RoutingRequest request);
}
