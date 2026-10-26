using System.Diagnostics;

namespace AiProxy.Hosting;

/// <summary>Starts this executable again in guard mode, with a pipe whose closing signals that the owner is gone.</summary>
public sealed class ProcessSystemProxyGuardLauncher : ISystemProxyGuardLauncher
{
    /// <inheritdoc />
    public ISystemProxyGuardSession Launch(string dataDirectory, Action guardExited)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(guardExited);
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("The ai-proxy executable path is unknown, so the system proxy guard cannot start.");
        var start = new ProcessStartInfo(processPath)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(processPath, Environment.GetCommandLineArgs()[0], dataDirectory)) start.ArgumentList.Add(argument);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var session = new Session(process, guardExited);
        process.Exited += session.OnExited;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The system proxy guard process did not start.");
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return session;
    }

    /// <summary>The arguments that start guard mode; the dotnet muxer needs the assembly path first.</summary>
    public static IReadOnlyList<string> BuildArguments(string processPath, string entryAssemblyPath, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fileName = processPath[(processPath.LastIndexOfAny(['/', '\\']) + 1)..];
        var isMuxer = string.Equals(Path.GetFileNameWithoutExtension(fileName), "dotnet", StringComparison.OrdinalIgnoreCase);
        return isMuxer
            ? [entryAssemblyPath, GuardCommand.Argument, dataDirectory]
            : [GuardCommand.Argument, dataDirectory];
    }

    private sealed class Session(Process process, Action guardExited) : ISystemProxyGuardSession
    {
        private volatile bool _closing;

        public void ReportRestored()
        {
            _closing = true;
            process.StandardInput.WriteLine(SystemProxyGuard.RestoredLine);
            process.StandardInput.Flush();
        }

        public void Dispose()
        {
            _closing = true;
            try
            {
                process.StandardInput.Close();
                process.WaitForExit(TimeSpan.FromSeconds(2));
            }
            finally
            {
                process.Dispose();
            }
        }

        public void OnExited(object? sender, EventArgs e)
        {
            if (!_closing) guardExited();
        }
    }
}