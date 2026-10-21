namespace AiProxy.Core.Routing;

/// <summary>An operator's rule: a path prefix and the upstream that serves it.</summary>
/// <param name="Prefix">The path prefix the rule covers.</param>
/// <param name="Upstream">The upstream the traffic under the prefix goes to.</param>
public sealed record PassThroughUpstream(string Prefix, Uri Upstream);
