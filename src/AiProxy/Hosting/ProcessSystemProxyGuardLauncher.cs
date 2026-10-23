using System.Diagnostics;
using System.Text;

namespace AiProxy.Hosting;

/// <summary>Starts this executable again in guard mode, with a pipe whose closing signals that the owner is gone.</summary>
public sealed class ProcessSystemProxyGuardLauncher : ISystemProxyGuardLauncher
{
    /// <inheritdoc />
    public ISystemProxyGuardSession Launch(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The ai-proxy executable path is unknown, so the system proxy guard cannot start.");
        var start = new ProcessStartInfo(processPath)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        start.ArgumentList.Add(GuardCommand.Argument);
        start.ArgumentList.Add(dataDirectory);
        var process = Process.Start(start)
            ?? throw new InvalidOperationException("The system proxy guard process did not start.");
        return new Session(process);
    }

    private sealed class Session(Process process) : ISystemProxyGuardSession
    {
        public void ReportRestored()
        {
            process.StandardInput.WriteLine(SystemProxyGuard.RestoredLine);
            process.StandardInput.Flush();
        }

        public void Dispose()
        {
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
    }
}
