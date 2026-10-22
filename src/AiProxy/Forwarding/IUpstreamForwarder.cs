using Microsoft.AspNetCore.Http;

namespace AiProxy.Forwarding;

/// <summary>Relays a request to an upstream and the response back to the caller.</summary>
public interface IUpstreamForwarder
{
    /// <summary>Relays the request, and writes the response onto the caller's connection.</summary>
    Task ForwardAsync(HttpContext context, UpstreamCall call, CancellationToken cancellationToken);
}
