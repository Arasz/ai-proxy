using System.Diagnostics;
using System.Security.Cryptography;
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
        if (!OperatingSystem.IsLinux() || !File.Exists(FindCertutil()))
        {
            return;
        }

        foreach (var database in Databases(homeDirectory))
        {
            foreach (var nickname in Nicknames(database))
            {
                var exported = Run(FindCertutil(), ["-L", "-d", $"sql:{database}", "-n", nickname, "-a"]);
                if (exported.ExitCode != 0 || !TryLoadCertificate(exported.Output, out var certificate) || certificate is null)
                {
                    continue;
                }

                using (certificate)
                {
                    if (string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        Run(FindCertutil(), ["-D", "-d", $"sql:{database}", "-n", nickname], requireSuccess: true);
                    }
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

    private static IEnumerable<string> Nicknames(string database)
    {
        var result = Run(FindCertutil(), ["-L", "-d", $"sql:{database}"]);
        if (result.ExitCode != 0)
        {
            yield break;
        }

        foreach (var line in result.Output.Split('\n'))
        {
            var match = NicknameRow().Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                yield return match.Groups["name"].Value.TrimEnd();
            }
        }
    }

    private static bool TryLoadCertificate(string pem, out X509Certificate2? certificate)
    {
        try
        {
            certificate = X509Certificate2.CreateFromPem(pem);
            return true;
        }
        catch (CryptographicException)
        {
            certificate = null;
            return false;
        }
    }

    private static string FindCertutil()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var executable = Path.Combine(directory, "certutil");
            if (File.Exists(executable))
            {
                return executable;
            }
        }

        return "certutil";
    }

    private static (int ExitCode, string Output) Run(string executable, IReadOnlyList<string> arguments, bool requireSuccess = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return (-1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeoutMilliseconds))
        {
            process.Kill(true);
            process.WaitForExit();
            return (-1, string.Empty);
        }

        Task.WaitAll(output, error);
        if (requireSuccess && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"certutil failed ({process.ExitCode}): {error.Result}");
        }

        return (process.ExitCode, output.Result);
    }

    [GeneratedRegex(@"^(?<name>.+?)\s{2,}[A-Za-z,]{3,}$")]
    private static partial Regex NicknameRow();
}
