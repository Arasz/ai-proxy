namespace AiProxy.Hosting.Proxy;

/// <summary>Picks the data directory for lifecycle commands the way the proxy does: option, then <c>Proxy:DataDirectory</c> from environment variables, then the default.</summary>
public static class ProxyDataDirectory
{
    public static string Resolve(string? option, IConfiguration configuration)
    {
        var chosen = !string.IsNullOrWhiteSpace(option) ? option : configuration["Proxy:DataDirectory"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(chosen) ? ProxyRuntimeOptions.DefaultDataDirectory : chosen);
    }

    /// <summary>Resolves with <paramref name="variable" /> standing in for the process environment, or from the environment itself when it is null.</summary>
    public static string Resolve(string? option, Func<string, string?>? variable)
    {
        if (variable is null)
        {
            return FromEnvironment(option);
        }

        var chosen = !string.IsNullOrWhiteSpace(option) ? option : variable("Proxy__DataDirectory") ?? variable("PROXY__DATADIRECTORY");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(chosen) ? ProxyRuntimeOptions.DefaultDataDirectory : chosen);
    }

    /// <summary>Resolves from the process environment; names match case-insensitively, as for serve.</summary>
    public static string FromEnvironment(string? option) => Resolve(option, new ConfigurationBuilder().AddEnvironmentVariables().Build());
}
