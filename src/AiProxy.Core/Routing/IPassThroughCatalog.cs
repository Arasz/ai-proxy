namespace AiProxy.Core.Routing;

/// <summary>The operator's path-prefix rules, which carry traffic that is not provider traffic.</summary>
public interface IPassThroughCatalog
{
    /// <summary>Whether a path falls under a configured prefix, and the upstream it goes to.</summary>
    bool TryGetUpstream(string path, out Uri upstream);
}
