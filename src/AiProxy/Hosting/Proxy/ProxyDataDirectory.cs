namespace AiProxy.Hosting.Proxy;

/// <summary>Picks the data directory for lifecycle commands the way the proxy does: option, then <c>Proxy:DataDirectory</c> from environment variables, then the default.</summary>
public static class ProxyDataDirectory
{
    public static string Resolve(string? option, IConfiguration configuration)
    {
        var chosen = !string.IsNullOrWhiteSpace(option) ? option : configuration["Proxy:DataDirectory"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(chosen) ? ProxyRuntimeOptions.DefaultDataDirectory : chosen);
    }

    /// <summary>Resolves from the process environment; names match case-insensitively, as for serve.</summary>
    public static string FromEnvironment(string? option)
    {
        return Resolve(option, new ConfigurationBuilder().AddEnvironmentVariables().Build());
    }
}
