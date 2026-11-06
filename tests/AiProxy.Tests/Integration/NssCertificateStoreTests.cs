using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiProxy.Hosting.Integration;

namespace AiProxy.Tests.Integration;

public sealed class NssCertificateStoreTests : IAsyncDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ai-proxy-tests", Guid.CreateVersion7().ToString("N"));
    private string Database => Path.Combine(_home, ".pki", "nssdb");

    [Fact]
    public void Removing_from_a_missing_user_database_is_safe()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The NSS user store cleanup runs on Linux.");

        new NssCertificateStore().Remove("0000000000000000000000000000000000000000", _home);
    }

    [Fact]
    public async Task Removing_a_replaced_root_from_the_user_database_keeps_unrelated_roots()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The NSS user store cleanup runs on Linux.");
        Assert.SkipWhen(!await IsAvailableAsync("certutil"), "Install libnss3-tools to run the NSS database test.");
        Directory.CreateDirectory(Database);
        await RunAsync("certutil", ["-N", "--empty-password", "-d", $"sql:{Database}"]);

        using var oldRoot = Certificate("old proxy root");
        using var currentRoot = Certificate("current proxy root");
        using var unrelated = Certificate("unrelated root");
        var oldPem = await WritePemAsync(oldRoot, "old.pem");
        var currentPem = await WritePemAsync(currentRoot, "current.pem");
        var unrelatedPem = await WritePemAsync(unrelated, "unrelated.pem");
        await ImportAsync("old proxy root", oldPem);
        await ImportAsync("current proxy root", currentPem);
        await ImportAsync("unrelated root", unrelatedPem);

        new NssCertificateStore().Remove(oldRoot.Thumbprint!, _home);

        Assert.False(await ContainsAsync("old proxy root"));
        Assert.True(await ContainsAsync("current proxy root"));
        Assert.True(await ContainsAsync("unrelated root"));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task ImportAsync(string nickname, string pemPath)
    {
        await RunAsync("certutil", ["-A", "-d", $"sql:{Database}", "-n", nickname, "-t", "CT,C,C", "-i", pemPath]);
    }

    private async Task<bool> ContainsAsync(string nickname)
    {
        return await RunAsync("certutil", ["-L", "-d", $"sql:{Database}", "-n", nickname], allowFailure: true) == 0;
    }

    private async Task<string> WritePemAsync(X509Certificate2 certificate, string name)
    {
        var path = Path.Combine(_home, name);
        await File.WriteAllTextAsync(path, PemEncoding.WriteString("CERTIFICATE", certificate.RawData), TestContext.Current.CancellationToken);
        return path;
    }

    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static async Task<bool> IsAvailableAsync(string executable)
    {
        try
        {
            return await RunAsync(executable, ["-H"], allowFailure: true) is 0 or 1;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(stdout, stderr);
        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} failed ({process.ExitCode}): {stderr.Result}\n{stdout.Result}");
        }

        return process.ExitCode;
    }
}
