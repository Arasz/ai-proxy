namespace AiProxy.Hosting.Proxy;

/// <summary>Picks the data directory for lifecycle commands the way the proxy does: option, then <c>Proxy__DataDirectory</c>, then the default.</summary>
public static class ProxyDataDirectory
{
    public const string Variable = "Proxy__DataDirectory";

    public static string Resolve(string? option, Func<string, string?> variable)
    {
        var chosen = !string.IsNullOrWhiteSpace(option) ? option : variable(Variable);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(chosen) ? ProxyRuntimeOptions.DefaultDataDirectory : chosen);
    }
}
