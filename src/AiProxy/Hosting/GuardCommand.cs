namespace AiProxy.Hosting;

/// <summary>The <c>--guard-system-proxy</c> mode: waits for the owner's pipe to end and, if it died, clears the system proxy under the marker lock.</summary>
public sealed class GuardCommand
{
    /// <summary>The first argument that selects guard mode; the second is the data directory.</summary>
    public const string Argument = "--guard-system-proxy";

    private readonly Action _clear;

    private readonly string _dataDirectory;
    private readonly TimeSpan _lockWait;

    /// <summary>Creates the guard for the marker in <paramref name="dataDirectory" />; <paramref name="lockWait" /> bounds the wait for that marker.</summary>
    public GuardCommand(string dataDirectory, Action clear, TimeSpan lockWait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(clear);
        _dataDirectory = dataDirectory;
        _clear = clear;
        _lockWait = lockWait;
    }

    /// <summary>True when <paramref name="args" /> ask for guard mode.</summary>
    public static bool IsRequested(string[] args)
    {
        return args is [Argument, _];
    }

    /// <summary>Reads <paramref name="input" /> until it ends and returns the process exit code.</summary>
    public int Run(TextReader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            new SystemProxyGuard(ClearUnderLock).Run(input);
            return 0;
        }
        catch (Exception exception)
        {
            TryReport($"ai-proxy guard could not clear the system proxy: {exception.Message}");
            return 1;
        }
    }

    private void ClearUnderLock()
    {
        SystemProxyMarker marker;
        try
        {
            marker = SystemProxyMarker.Acquire(_dataDirectory, _lockWait);
        }
        catch (InvalidOperationException)
        {
            // A new instance holds the marker and has already dealt with the crash.
            return;
        }

        using (marker)
        {
            if (marker.PreviousRunDied) _clear();

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