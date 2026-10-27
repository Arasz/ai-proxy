using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AiProxy.Core.Gain;

namespace AiProxy.Hosting;

/// <summary>Finds an older same-user proxy by its exact held marker and executable entry point.</summary>
internal sealed partial class LegacyProxy(string directory, int? requestedProxyPort = null)
{
    internal string[] ListenerUrls { get; private set; } = [];

    private string MarkerPath => Path.Combine(directory, SystemProxyMarker.FileName);

    internal async Task<ProxyControl.ProxyInfo?> FindAsync(CancellationToken token)
    {
        if (!File.Exists(MarkerPath))
        {
            return null;
        }

        try
        {
            using var available = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return null;
        }
        catch (IOException)
        {
            // The operating system, rather than marker contents or a listening port, identifies the holder.
        }

        var holders = await HoldersAsync(token);
        if (holders.Length != 1 || holders[0] == Environment.ProcessId)
        {
            throw UnknownOwner();
        }

        using var process = Process.GetProcessById(holders[0]);
        var startToken = new ProcessIdentity(process).StartToken;
        if ((await ArgumentsAsync(process, token)).Contains(GuardCommand.Argument, StringComparer.Ordinal))
        {
            await WaitForMarkerCleanupAsync(token);
            return null;
        }

        if (!await IsProxyAsync(process, token))
        {
            throw UnknownOwner(process.Id);
        }

        var endpoints = await ListenersAsync(process.Id, token);
        ListenerUrls = endpoints;
        var api = new List<string>();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        foreach (var endpoint in endpoints)
        {
            try
            {
                using var response = await http.GetAsync($"{endpoint}/health", token);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (response.IsSuccessStatusCode && body.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ok")
                {
                    api.Add(endpoint);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                // A forward listener is not the management health endpoint.
            }
        }

        string? proxy = null;
        if (!OperatingSystem.IsWindows())
        {
            var marker = (await ExecuteAsync("/bin/cat", [MarkerPath], token)).Trim();
            if (IPEndPoint.TryParse(marker, out var address) && endpoints.Contains(NormalizeEndpoint(address.ToString())))
            {
                proxy = NormalizeEndpoint(address.ToString());
            }
        }
        else
        {
            var candidates = endpoints.Except(api).Where(endpoint => new Uri(endpoint).Port != new Uri(GainApi.DefaultEndpoint).Port).ToArray();
            proxy = candidates.Length == 1 ? candidates[0] :
                candidates.SingleOrDefault(endpoint => new Uri(endpoint).Port == (requestedProxyPort ?? ProxyRuntimeOptions.DefaultPort));
        }

        if (proxy is null || api.Count == 0)
        {
            await Console.Error.WriteLineAsync("ai-proxy: Some legacy listener settings could not be identified. Explicit replacement configuration or loopback defaults will be used for those listeners.");
        }

        proxy ??= $"http://127.0.0.1:{requestedProxyPort ?? ProxyRuntimeOptions.DefaultPort}";
        return new ProxyControl.ProxyInfo("ai-proxy", 0, "legacy", process.Id, process.StartTime.ToUniversalTime(), proxy,
            api.Count > 0 ? [.. api] : [GainApi.DefaultEndpoint], startToken);
    }

    internal async Task StopAsync(ProxyControl.ProxyInfo owner, CancellationToken token)
    {
        using var process = Process.GetProcessById(owner.Pid);
        await VerifyAsync(process, owner, token);
        EnsureBirth(process, owner);
        if (OperatingSystem.IsWindows())
        {
            process.CloseMainWindow();
        }
        else if (Kill(process.Id, 15) != 0)
        {
            throw new IOException($"Cannot signal legacy ai-proxy PID {process.Id}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        using var graceful = CancellationTokenSource.CreateLinkedTokenSource(token);
        graceful.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(graceful.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await VerifyAsync(process, owner, token, requireMarker: false);
            EnsureBirth(process, owner);
            process.Kill();
            using var forced = CancellationTokenSource.CreateLinkedTokenSource(token);
            forced.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(forced.Token);
        }

        await WaitForMarkerCleanupAsync(token);
    }

    private async Task WaitForMarkerCleanupAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (File.Exists(MarkerPath))
        {
            try
            {
                using var marker = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
        }
    }

    private async Task VerifyAsync(Process process, ProxyControl.ProxyInfo owner, CancellationToken token, bool requireMarker = true)
    {
        process.Refresh();
        if (!MatchesIdentity(owner.Pid, owner.ProcessStartToken, process.Id, new ProcessIdentity(process).StartToken) ||
            (requireMarker && !(await HoldersAsync(token)).SequenceEqual([owner.Pid])) ||
            !await IsCurrentUserAsync(process, token) || !await IsProxyAsync(process, token))
        {
            throw new IOException("The legacy ai-proxy owner identity changed. No shutdown signal was sent.");
        }
    }

    private static void EnsureBirth(Process process, ProxyControl.ProxyInfo owner)
    {
        using var current = Process.GetProcessById(process.Id);
        if (current.HasExited || !MatchesIdentity(owner.Pid, owner.ProcessStartToken, current.Id, new ProcessIdentity(current).StartToken))
        {
            throw new IOException("The legacy ai-proxy owner identity changed. No shutdown signal was sent.");
        }
    }

    internal static bool MatchesIdentity(int expectedPid, string expectedStart, int actualPid, string actualStart) =>
        expectedPid > 0 && expectedPid != Environment.ProcessId && expectedPid == actualPid &&
        !string.IsNullOrWhiteSpace(expectedStart) && expectedStart == actualStart;

    private async Task<int[]> HoldersAsync(CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsLegacyProxy.Holders(MarkerPath);
        }

        if (OperatingSystem.IsMacOS())
        {
            var output = await ExecuteAsync("/usr/sbin/lsof", ["-a", "-u", Environment.UserName, "-Fp", "--", MarkerPath], token, allowNoMatches: true);
            return [.. output.Split('\n').Where(line => line.StartsWith('p')).Select(line => int.Parse(line.AsSpan(1), CultureInfo.InvariantCulture)).Distinct()];
        }

        var result = new List<int>();
        foreach (var candidate in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(candidate), out var pid))
            {
                continue;
            }

            try
            {
                var uids = File.ReadLines(Path.Combine(candidate, "status")).Single(line => line.StartsWith("Uid:", StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
                if (uids.Length != 4 || uids.Any(uid => uid != GetEffectiveUserId().ToString(CultureInfo.InvariantCulture)))
                {
                    continue;
                }

                if (Directory.EnumerateFiles(Path.Combine(candidate, "fd")).Any(file => new FileInfo(file).LinkTarget == MarkerPath))
                {
                    result.Add(pid);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Processes can exit during discovery; inaccessible processes cannot be verified.
            }
        }

        return [.. result];
    }

    private static async Task<bool> IsCurrentUserAsync(Process process, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsLegacyProxy.IsCurrentUser(process);
        }

        if (OperatingSystem.IsLinux())
        {
            var uids = File.ReadLines($"/proc/{process.Id}/status").Single(line => line.StartsWith("Uid:", StringComparison.Ordinal))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            return uids.Length == 4 && uids.All(uid => uid == GetEffectiveUserId().ToString(CultureInfo.InvariantCulture));
        }

        var output = await ExecuteAsync("/usr/sbin/lsof", ["-a", "-p", process.Id.ToString(CultureInfo.InvariantCulture), "-u", Environment.UserName, "-Fp"], token, allowNoMatches: true);
        return output.Split('\n').Contains($"p{process.Id.ToString(CultureInfo.InvariantCulture)}", StringComparer.Ordinal);
    }

    private static async Task<bool> IsProxyAsync(Process process, CancellationToken token)
    {
        if (OperatingSystem.IsWindows() && !WindowsLegacyProxy.IsCurrentUser(process))
        {
            return false;
        }

        var executable = process.MainModule?.FileName;
        if (executable is null)
        {
            return false;
        }

        var arguments = await ArgumentsAsync(process, token);
        if (arguments.Length == 0 || arguments.Contains(GuardCommand.Argument, StringComparer.Ordinal))
        {
            return false;
        }

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var workingDirectory = OperatingSystem.IsLinux()
                ? new DirectoryInfo($"/proc/{process.Id}/cwd").LinkTarget
                : OperatingSystem.IsMacOS()
                    ? (await ExecuteAsync("/usr/sbin/lsof", ["-a", "-p", process.Id.ToString(CultureInfo.InvariantCulture), "-d", "cwd", "-Fn"], token)).Split('\n').FirstOrDefault(line => line.StartsWith('n'))?[1..]
                    : null;
            var entry = ManagedEntry(arguments, workingDirectory);
            return entry is not null && IsEntryAssembly(entry);
        }

        return IsNativeEntry(executable);
    }

    private static async Task<string[]> ArgumentsAsync(Process process, CancellationToken token)
    {
        if (OperatingSystem.IsLinux())
        {
            return (await File.ReadAllTextAsync($"/proc/{process.Id}/cmdline", token)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        var command = OperatingSystem.IsWindows()
            ? await ExecuteAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", $"(Get-CimInstance Win32_Process -Filter 'ProcessId = {process.Id}').CommandLine"], token)
            : await ExecuteAsync("/bin/ps", ["-ww", "-p", process.Id.ToString(CultureInfo.InvariantCulture), "-o", "args="], token);
        return SplitCommand(command.Trim());
    }

    internal static bool IsNativeEntry(string executable)
    {
        var name = Path.GetFileName(executable);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (!name.Equals("AiProxy", StringComparison.OrdinalIgnoreCase) && !name.Equals("ai-proxy", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var assembly = Path.Combine(Path.GetDirectoryName(executable)!, "AiProxy.dll");
        if (File.Exists(assembly))
        {
            return IsEntryAssembly(assembly);
        }

        var normalized = executable.Replace('\\', '/');
        return normalized.Contains("/.dotnet/tools/.store/", StringComparison.Ordinal) &&
               normalized.Contains("/ai-proxy.", StringComparison.Ordinal) &&
               normalized.Contains("/tools/net", StringComparison.Ordinal) &&
               normalized.EndsWith("/AiProxy", StringComparison.Ordinal);
    }

    internal static bool IsEntryAssembly(string path)
    {
        if (!Path.GetFileName(path).Equals("AiProxy.dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return AssemblyName.GetAssemblyName(path).Name == "AiProxy";
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string? ManagedEntry(string[] arguments, string? workingDirectory)
    {
        var index = 1;
        if (arguments.Length > index && arguments[index] == "exec")
        {
            index++;
        }

        while (index < arguments.Length && arguments[index].StartsWith('-'))
        {
            if (arguments[index] is not ("--depsfile" or "--runtimeconfig" or "--additionalprobingpath" or "--additional-deps" or "--fx-version" or "--roll-forward"))
            {
                return null;
            }

            index += 2;
        }

        if (index >= arguments.Length || !Path.GetFileName(arguments[index]).Equals("AiProxy.dll", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(arguments[index]))
        {
            return arguments[index];
        }

        return workingDirectory is null ? null : Path.GetFullPath(arguments[index], workingDirectory);
    }

    private static string[] SplitCommand(string command)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(command, "\"([^\"]*)\"|'([^']*)'|([^\\s]+)");
        return [.. matches.Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value)];
    }

    internal static async Task<IPEndPoint[]> MacListenersAsync(CancellationToken token)
    {
        var output = await ExecuteAsync("/usr/sbin/lsof", ["-nP", "-iTCP", "-sTCP:LISTEN", "-Ftn"], token, allowNoMatches: true);
        var endpoints = new List<IPEndPoint>();
        var ipv6 = false;
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith('t'))
            {
                ipv6 = line == "tIPv6";
            }
            else if (line.StartsWith('n'))
            {
                var value = line[1..];
                if (value.StartsWith("*:", StringComparison.Ordinal))
                {
                    value = (ipv6 ? "[::]" : "0.0.0.0") + value[1..];
                }

                if (!IPEndPoint.TryParse(value, out var endpoint))
                {
                    throw new IOException("Cannot parse the operating system's TCP listener inventory. No owner was stopped.");
                }

                endpoints.Add(endpoint);
            }
        }

        return [.. endpoints.Distinct()];
    }

    private static async Task<string[]> ListenersAsync(int pid, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var output = await ExecuteAsync("netstat.exe", ["-ano", "-p", "tcp"], token);
            return [.. output.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(fields => fields.Length == 5 && fields[3] == "LISTENING" && fields[4] == pid.ToString(CultureInfo.InvariantCulture))
                .Select(fields => NormalizeEndpoint(fields[1])).Distinct()];
        }

        if (OperatingSystem.IsMacOS())
        {
            var output = await ExecuteAsync("/usr/sbin/lsof", ["-a", "-p", pid.ToString(CultureInfo.InvariantCulture), "-iTCP", "-sTCP:LISTEN", "-nP", "-Fn"], token, allowNoMatches: true);
            return [.. output.Split('\n').Where(line => line.StartsWith('n')).Select(line => NormalizeEndpoint(line[1..])).Distinct()];
        }

        var inodes = Directory.EnumerateFiles($"/proc/{pid}/fd")
            .Select(file => new FileInfo(file).LinkTarget).Where(target => target?.StartsWith("socket:[", StringComparison.Ordinal) == true)
            .Select(target => target![8..^1]).ToHashSet();
        var endpoints = new List<string>();
        foreach (var protocol in new[] { "tcp", "tcp6" })
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/net/{protocol}").Skip(1))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields[3] != "0A" || !inodes.Contains(fields[9]))
                {
                    continue;
                }

                var parts = fields[1].Split(':');
                var bytes = Convert.FromHexString(parts[0]);
                if (BitConverter.IsLittleEndian)
                {
                    for (var index = 0; index < bytes.Length; index += 4)
                    {
                        Array.Reverse(bytes, index, 4);
                    }
                }

                var address = new IPAddress(bytes);
                endpoints.Add(NormalizeEndpoint(new IPEndPoint(address, int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString()));
            }
        }

        return [.. endpoints.Distinct()];
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var normalized = endpoint.Replace("*:", "127.0.0.1:", StringComparison.Ordinal).Replace("0.0.0.0:", "127.0.0.1:", StringComparison.Ordinal).Replace("[::]:", "[::1]:", StringComparison.Ordinal);
        return $"http://{normalized}";
    }

    internal static async Task<string> ExecuteAsync(string executable, string[] arguments, CancellationToken token, bool allowNoMatches = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("Cannot start legacy owner discovery.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var stderr = await error;
            var stdout = await output;
            if (process.ExitCode != 0 && !(allowNoMatches && process.ExitCode == 1 && stdout.Length == 0 && stderr.Length == 0))
            {
                throw new IOException($"Operating system discovery failed with exit code {process.ExitCode}. No owner was stopped.");
            }

            return stdout;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    private static IOException UnknownOwner(int? pid = null) => new($"The system proxy marker holder{(pid is null ? "" : $" (PID {pid})")} is not a verified same-user ai-proxy entry process. Stop its owner manually before retrying.");

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
