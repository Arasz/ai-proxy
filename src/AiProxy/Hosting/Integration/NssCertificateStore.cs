using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace AiProxy.Hosting.Integration;

/// <summary>Removes a replaced proxy root from the user's NSS databases.</summary>
internal sealed partial class NssCertificateStore
{
    private const int CommandTimeoutMilliseconds = 5000;

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

        foreach (var database in databases)
        {
            foreach (var nickname in Nicknames(database, certutil))
            {
                var exported = Run(certutil, ["-L", "-d", $"sql:{database}", "-n", nickname, "-a"]);
                using var certificate = X509Certificate2.CreateFromPem(exported);

                if (string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    Run(certutil, ["-D", "-d", $"sql:{database}", "-n", nickname]);
                }
            }
        }
    }

    private static IEnumerable<string> Databases(string homeDirectory)
    {
        var candidates = new List<string> { Path.Combine(homeDirectory, ".pki", "nssdb") };
        candidates.AddRange(Profiles(Path.Combine(homeDirectory, ".mozilla", "firefox")));
        candidates.AddRange(Profiles(Path.Combine(homeDirectory, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox")));
        return candidates.Where(static path => File.Exists(Path.Combine(path, "cert9.db"))).Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> Profiles(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).Where(static path => File.Exists(Path.Combine(path, "cert9.db")))
            : [];
    }

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

    private static string? FindCertutil()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var executable = Path.Combine(directory, "certutil");
            if (File.Exists(executable))
            {
                return executable;
            }
        }

        return null;
    }

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
