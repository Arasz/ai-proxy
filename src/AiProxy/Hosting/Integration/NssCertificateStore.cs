using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace AiProxy.Hosting.Integration;

/// <summary>Removes a replaced proxy root from the user's NSS databases.</summary>
internal sealed partial class NssCertificateStore
{
    private const int CommandTimeoutMilliseconds = 5000;
    private static readonly string[] TrustedCertutilPaths = ["/usr/bin/certutil", "/usr/sbin/certutil", "/bin/certutil"];

    /// <summary>Deletes only certificates whose DER thumbprint matches <paramref name="thumbprint" />.</summary>
    public void Remove(string thumbprint, string homeDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var databases = Databases(homeDirectory).ToArray();
        if (databases.Length == 0)
        {
            return;
        }

        var certutil = FindCertutil();
        if (certutil is null)
        {
            throw new InvalidOperationException("certutil is required to remove a replaced proxy root from the user's NSS databases.");
        }

        var deletionFailures = new List<string>();
        foreach (var database in databases)
        {
            foreach (var nickname in Nicknames(database, certutil))
            {
                var exported = Run(certutil, ["-L", "-d", $"sql:{database}", "-n", nickname, "-a"]);
                using var certificate = X509Certificate2.CreateFromPem(exported);

                if (string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Run(certutil, ["-D", "-d", $"sql:{database}", "-n", nickname]);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                    {
                        deletionFailures.Add($"{database}, nickname '{nickname}': {exception.Message}");
                    }
                }
            }
        }

        if (deletionFailures.Count > 0)
        {
            throw new InvalidOperationException($"Could not remove the replaced proxy root from NSS: {string.Join("; ", deletionFailures)}");
        }
    }

    /// <summary>Finds existing NSS databases in the user's shared store and Firefox profiles.</summary>
    private static IEnumerable<string> Databases(string homeDirectory)
    {
        var candidates = new List<string> { Path.Combine(homeDirectory, ".pki", "nssdb") };
        candidates.AddRange(Profiles(Path.Combine(homeDirectory, ".mozilla", "firefox")));
        candidates.AddRange(Profiles(Path.Combine(homeDirectory, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox")));
        return candidates.Where(static path => File.Exists(Path.Combine(path, "cert9.db"))).Distinct(StringComparer.Ordinal);
    }

    /// <summary>Finds direct child profile directories that contain an SQL NSS database.</summary>
    private static IEnumerable<string> Profiles(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).Where(static path => File.Exists(Path.Combine(path, "cert9.db")))
            : [];
    }

    /// <summary>Lists certificate nicknames in one NSS database.</summary>
    private static IEnumerable<string> Nicknames(string database, string certutil)
    {
        var result = Run(certutil, ["-L", "-d", $"sql:{database}"]);

        foreach (var line in result.Split('\n'))
        {
            var match = NicknameRow().Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                yield return match.Groups["name"].Value.TrimEnd();
            }
        }
    }

    /// <summary>Finds certutil only in standard absolute system locations.</summary>
    private static string? FindCertutil()
    {
        foreach (var executable in TrustedCertutilPaths)
        {
            if (File.Exists(executable))
            {
                return executable;
            }
        }

        return null;
    }

    /// <summary>Runs certutil with separate arguments and fails on timeout or a nonzero exit code.</summary>
    private static string Run(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            throw new InvalidOperationException("certutil could not be started.");
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeoutMilliseconds))
        {
            process.Kill(true);
            process.WaitForExit();
            throw new InvalidOperationException("certutil did not finish within five seconds.");
        }

        Task.WaitAll(output, error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"certutil failed ({process.ExitCode}): {error.Result}");
        }

        return output.Result;
    }

    [GeneratedRegex(@"^(?<name>.+?)\s{2,}[A-Za-z,]{3,}$")]
    private static partial Regex NicknameRow();
}
