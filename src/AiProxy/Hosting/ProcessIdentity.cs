using System.Diagnostics;
using System.Globalization;

namespace AiProxy.Hosting;

/// <summary>Reads a process start identity without converting Linux boot ticks to wall time.</summary>
internal sealed class ProcessIdentity(Process process)
{
    internal string StartToken => OperatingSystem.IsLinux()
        ? ParseLinuxStat(File.ReadAllText($"/proc/{process.Id}/stat"))
        : "native:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

    internal static string ParseLinuxStat(string stat)
    {
        var commandEnd = stat.LastIndexOf(')');
        var fields = commandEnd >= 0
            ? stat[(commandEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];
        if (fields.Length <= 19 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
        {
            throw new IOException("The kernel process start identity is unavailable. Stop the owner manually before retrying.");
        }

        return "linux:" + ticks.ToString(CultureInfo.InvariantCulture);
    }
}
