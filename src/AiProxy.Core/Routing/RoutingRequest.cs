using AiProxy.Core.Providers;

namespace AiProxy.Core.Routing;

/// <summary>The request a router decides about.</summary>
/// <param name="Path">The request path, without a query string.</param>
/// <param name="Headers">The request headers.</param>
/// <param name="Body">The request body, or null when it was not read.</param>
/// <param name="Detection">What the detector concluded about the request.</param>
public sealed record RoutingRequest(
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    Detection Detection);
