using AiProxy.Hosting.Marker;

namespace AiProxy.Hosting.Guard;

/// <summary>The <c>--guard-system-proxy</c> mode: waits for the owner's pipe to end and, if it died, clears the system proxy under the marker lock.</summary>
public sealed class GuardCommand(string dataDirectory, Func<Task> clearProxy, TimeSpan lockWait, TimeProvider timeProvider)
{
    /// <summary>The first argument that selects guard mode; the second is the data directory.</summary>
    public const string Argument = "--guard-system-proxy";
    /// <summary>True when <paramref name="args" /> ask for guard mode.</summary>
    public static bool IsRequested(string[] args) => args is [Argument, _];

    /// <summary>Reads <paramref name="input" /> until it ends and returns the process exit code.</summary>
    public async Task<int> Run(TextReader input)
    {
        try
        {
            var systemProxyGuard = new SystemProxyGuard(ClearUnderLock);
            await systemProxyGuard.Run(input);
            return 0;
        }
        catch (Exception exception)
        {
            TryReport($"ai-proxy guard could not clear the system proxy: {exception.Message}");
            return 1;
        }
    }

    private async Task ClearUnderLock()
    {
        SystemProxyMarker marker;
        try
        {
            marker = await SystemProxyMarker.Acquire(dataDirectory, lockWait, timeProvider);
        }
        catch (InvalidOperationException)
        {
            // A new instance holds the marker and has already dealt with the crash.
            return;
        }

        using (marker)
        {
            if (marker.PreviousRunDied)
            {
                await clearProxy();
            }

            marker.Delete();
        }
    }

    private static void TryReport(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (Exception)
        {
            // The terminal can be gone; the marker stays, so the next start still clears.
        }
    }
}
