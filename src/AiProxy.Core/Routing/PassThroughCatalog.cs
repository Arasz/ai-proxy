namespace AiProxy.Core.Routing;

/// <summary>The operator's path-prefix rules, matched on whole path segments.</summary>
public sealed class PassThroughCatalog : IPassThroughCatalog
{
    private readonly IReadOnlyList<PassThroughUpstream> _rules;

    /// <summary>Holds the configured rules.</summary>
    public PassThroughCatalog(IEnumerable<PassThroughUpstream> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _rules = rules.ToArray();
    }

    /// <summary>Whether a path falls under a configured prefix, and the upstream it goes to.</summary>
    public bool TryGetUpstream(string path, out Uri upstream)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (var rule in _rules)
        {
            if (path.Equals(rule.Prefix, StringComparison.Ordinal) ||
                path.StartsWith(rule.Prefix + "/", StringComparison.Ordinal))
            {
                upstream = rule.Upstream;
                return true;
            }
        }

        upstream = null!;
        return false;
    }
}
