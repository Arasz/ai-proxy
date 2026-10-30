using AiProxy.Hosting.Guard;
using AiProxy.Hosting.Proxy;

namespace AiProxy.Cli;

/// <summary>What the process does for a given command line.</summary>
public enum EntryRoute
{
    /// <summary>Run the system-proxy guard.</summary>
    Guard,
    /// <summary>Run a CLI command, help or version.</summary>
    Command,
    /// <summary>Start the proxy.</summary>
    Serve,
    /// <summary>Print usage and exit 2.</summary>
    Usage
}

/// <summary>Decides which mode a command line selects, before anything starts.</summary>
public static class EntryPoint
{
    /// <summary>Exit code for an unrecognised command line.</summary>
    public const int UsageExitCode = 2;

    /// <summary>The usage text for the start mode.</summary>
    public const string StartUsage = "Start: ai-proxy [--restart] [--key=value | --key value ...]\n  --restart  Gracefully replace the owned proxy instance, waiting for its exit.\n  --version  Print the product version.";

    /// <summary>The product version, from the VERSION file at build time.</summary>
    public static string ProductVersion { get; } = ReadVersion();

    /// <summary>Selects the mode for <paramref name="args" />; start mode accepts only <c>--key=value</c> and <c>--key value</c> pairs plus <c>--restart</c>.</summary>
    public static EntryRoute Route(string[] args)
    {
        if (GuardCommand.IsRequested(args))
        {
            return EntryRoute.Guard;
        }

        if (CliApplication.IsCommandInvocation(args))
        {
            return EntryRoute.Command;
        }

        string[] rest = [.. args.Where(static argument => argument != ProxyCommand.RestartFlag)];
        for (var index = 0; index < rest.Length; index++)
        {
            var argument = rest[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                return EntryRoute.Usage;
            }

            if (argument.Contains('='))
            {
                continue;
            }

            if (index + 1 >= rest.Length || rest[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return EntryRoute.Usage;
            }

            index++;
        }

        return EntryRoute.Serve;
    }

    private static string ReadVersion()
    {
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(EntryPoint).Assembly)?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }
}
