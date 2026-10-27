using System.Diagnostics;
using System.Globalization;

namespace AiProxy.Hosting.Marker;

public sealed class UnixMarkerOwnerSearch(string executable) : IMarkerOwnerSearch
{
    private const int MarkerDiscoveryTimeoutMilliseconds = 3000;

    public async ValueTask<int?> FindMarkerOwner(string path)
    {
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(KnownNames.MarkerDiscovery.ProcessIds);
            start.ArgumentList.Add(KnownNames.MarkerDiscovery.EndOptions);
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(MarkerDiscoveryTimeoutMilliseconds))
            {
                process.Kill();
                return null;
            }

            if (process.ExitCode is not 0)
            {
                return null;
            }

            _ = await error;
            return MarkerOwnerPid(await output);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    internal static int? MarkerOwnerPid(string pidCheckOutput)
    {
        var pids = pidCheckOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static candidate => int.TryParse(candidate, CultureInfo.InvariantCulture, out var pid) ? (IsPid: true, PlatformID: pid) : (IsPid: false, PlatformID: 0))
            .Where(static value => value is { IsPid: true, PlatformID: > 0 })
            .Select(static value => value.PlatformID)
            .ToHashSet();

        return pids.Count != 1 ? null : pids.FirstOrDefault();
    }
}
