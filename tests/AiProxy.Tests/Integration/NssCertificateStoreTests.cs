using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AiProxy.Hosting.Integration;

namespace AiProxy.Tests.Integration;

/// <summary>Exercises NSS root cleanup in isolated per-test user databases.</summary>
public sealed class NssCertificateStoreTests : IAsyncDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ai-proxy-tests", Guid.CreateVersion7().ToString("N"));
    private string Database => Path.Combine(_home, ".pki", "nssdb");
    private string FirefoxDatabase => Path.Combine(_home, ".mozilla", "firefox", "profile.default");

    /// <summary>A missing NSS database stays absent and does not prevent cleanup.</summary>
    [Fact]
    public void Removing_from_a_missing_user_database_is_safe()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The NSS user store cleanup runs on Linux.");

        new NssCertificateStore().Remove("0000000000000000000000000000000000000000", _home);

        Assert.False(Directory.Exists(Database));
    }

    /// <summary>A corrupt existing NSS database reports the certutil failure.</summary>
    [Fact]
    public async Task A_database_query_failure_is_reported()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The NSS user store cleanup runs on Linux.");
        Assert.SkipWhen(!await IsAvailableAsync("certutil"), "Install libnss3-tools to run the NSS database test.");
        Directory.CreateDirectory(Database);
        await File.WriteAllTextAsync(Path.Combine(Database, "cert9.db"), "not an NSS database", TestContext.Current.CancellationToken);

        var error = Assert.Throws<InvalidOperationException>(() => new NssCertificateStore().Remove("0000000000000000000000000000000000000000", _home));

        Assert.Contains("certutil", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A failed deletion is reported while cleanup continues in other databases.</summary>
    [Fact]
    public async Task A_failed_deletion_is_reported_after_other_databases_are_cleaned()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The NSS user store cleanup runs on Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.SkipWhen(!await IsAvailableAsync("certutil"), "Install libnss3-tools to run the NSS database test.");
        Directory.CreateDirectory(Database);
        Directory.CreateDirectory(FirefoxDatabase);
        await InitializeDatabaseAsync(Database);
        await InitializeDatabaseAsync(FirefoxDatabase);

        using var oldRoot = Certificate("old proxy root");
        var pem = await WritePemAsync(oldRoot, "old-shared.pem");
        await ImportAsync("old proxy root", pem, Database);
        await ImportAsync("old proxy root", pem, FirefoxDatabase);

        SetDatabaseReadOnly(Database);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => new NssCertificateStore().Remove(oldRoot.Thumbprint!, _home));

            Assert.Contains(Database, error.Message, StringComparison.Ordinal);
            Assert.Contains("old proxy root", error.Message, StringComparison.Ordinal);
            Assert.True(await ContainsAsync("old proxy root", Database));
            Assert.False(await ContainsAsync("old proxy root", FirefoxDatabase));
        }
        finally
        {
            SetDatabaseWritable(Database);
        }
    }

    /// <summary>Only the matching old root is removed from the isolated NSS database.</summary>
    [Fact]
    public async Task Removing_a_replaced_root_from_the_user_database_keeps_unrelated_roots()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The NSS user store cleanup runs on Linux.");
        Assert.SkipWhen(!await IsAvailableAsync("certutil"), "Install libnss3-tools to run the NSS database test.");
        Directory.CreateDirectory(Database);
        await InitializeDatabaseAsync(Database);

        using var oldRoot = Certificate("old proxy root");
        using var currentRoot = Certificate("current proxy root");
        using var unrelated = Certificate("unrelated root");
        var oldPem = await WritePemAsync(oldRoot, "old.pem");
        var currentPem = await WritePemAsync(currentRoot, "current.pem");
        var unrelatedPem = await WritePemAsync(unrelated, "unrelated.pem");
        await ImportAsync("old proxy root", oldPem, Database);
        await ImportAsync("current proxy root", currentPem, Database);
        await ImportAsync("unrelated root", unrelatedPem, Database);

        new NssCertificateStore().Remove(oldRoot.Thumbprint!, _home);

        Assert.False(await ContainsAsync("old proxy root", Database));
        Assert.True(await ContainsAsync("current proxy root", Database));
        Assert.True(await ContainsAsync("unrelated root", Database));
    }

    /// <summary>Removes the isolated user profile after each test.</summary>
    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, true);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Creates a passwordless SQL NSS database at the given path.</summary>
    private static async Task InitializeDatabaseAsync(string database)
    {
        await RunAsync("certutil", ["-N", "--empty-password", "-d", $"sql:{database}"]);
    }

    /// <summary>Imports one certificate under its test nickname.</summary>
    private static async Task ImportAsync(string nickname, string pemPath, string database)
    {
        await RunAsync("certutil", ["-A", "-d", $"sql:{database}", "-n", nickname, "-t", "CT,C,C", "-i", pemPath]);
    }

    /// <summary>Checks whether certutil can find a nickname in one database.</summary>
    private static async Task<bool> ContainsAsync(string nickname, string database)
    {
        return await RunAsync("certutil", ["-L", "-d", $"sql:{database}", "-n", nickname], allowFailure: true) == 0;
    }

    /// <summary>Writes one test certificate as PEM into the isolated profile.</summary>
    private async Task<string> WritePemAsync(X509Certificate2 certificate, string name)
    {
        var path = Path.Combine(_home, name);
        await File.WriteAllTextAsync(path, PemEncoding.WriteString("CERTIFICATE", certificate.RawData), TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>Creates a short-lived self-signed test root with the requested subject.</summary>
    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>Checks whether a command can be started and returns a supported help status.</summary>
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

    /// <summary>Reaps a child process after cancellation while waiting or draining its output.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_async_stops_a_child_when_cancelled_while_it_waits_or_reads_output(bool writesOutput)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The cancellation fixture uses a POSIX executable.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_home);
        var scriptPath = Path.Combine(_home, "slow-command");
        var pidPath = Path.Combine(_home, "child.pid");
        var readingPath = Path.Combine(_home, "child.reading");
        // The reader, not the writer, creates the reading marker once it has consumed output.
        var output = writesOutput ? "while :; do printf '%1023s\\n' x; done" : "sleep 30";
        var script = $$"""
                     #!/bin/sh
                     printf '%s\n' "$$" > "$1.tmp" && mv "$1.tmp" "$1"
                     {{output}}
                     """;
        await File.WriteAllTextAsync(scriptPath, script, CancellationToken.None);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var cancellation = new CancellationTokenSource();
        var run = RunAsync(scriptPath, [pidPath], cancellationToken: cancellation.Token, readingPath: readingPath);
        try
        {
            await WaitForFileAsync(pidPath, run);
            Assert.True(File.Exists(pidPath), "The fixture command did not publish its PID before the timeout.");
            if (writesOutput)
            {
                await WaitForFileAsync(readingPath, run);
                Assert.True(File.Exists(readingPath), "The fixture output was not being read before the timeout.");
            }

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            var childId = int.Parse(await File.ReadAllTextAsync(pidPath, CancellationToken.None));
            Assert.True(HasExited(childId), $"certutil test process {childId} was left running after cancellation.");
        }
        finally
        {
            cancellation.Cancel();
            await ((Task)run).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>Waits up to five seconds for a fixture file, returning early when the command ends.</summary>
    private static async Task WaitForFileAsync(string path, Task run)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!File.Exists(path) && !run.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }

    [SupportedOSPlatform("linux")]
    /// <summary>Makes the certificate database itself read-only for a failed-delete fixture.</summary>
    private static void SetDatabaseReadOnly(string database)
    {
        File.SetUnixFileMode(Path.Combine(database, "cert9.db"), UnixFileMode.UserRead);
    }

    [SupportedOSPlatform("linux")]
    /// <summary>Restores database write access so fixture cleanup can remove it.</summary>
    private static void SetDatabaseWritable(string database)
    {
        File.SetUnixFileMode(Path.Combine(database, "cert9.db"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Checks whether a child PID has exited, including when its process record is gone.</summary>
    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    ///     Runs a test command and reaps it before propagating cancellation; <paramref name="readingPath" />, when given,
    ///     is created once standard output has been read.
    /// </summary>
    private static async Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        bool allowFailure = false,
        CancellationToken cancellationToken = default,
        string? readingPath = null)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var token = cancellationToken.CanBeCanceled ? cancellationToken : TestContext.Current.CancellationToken;
        var stdout = ReadOutputAsync(process.StandardOutput, readingPath, token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            try
            {
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }

            throw;
        }

        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} failed ({process.ExitCode}): {stderr.Result}\n{stdout.Result}");
        }

        return process.ExitCode;
    }

    /// <summary>Reads output to its end, creating <paramref name="readingPath" /> after the first chunk has been consumed.</summary>
    private static async Task<string> ReadOutputAsync(StreamReader reader, string? readingPath, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            output.Append(buffer, 0, read);
            if (readingPath is not null && !File.Exists(readingPath))
            {
                await File.WriteAllBytesAsync(readingPath, [], CancellationToken.None);
            }
        }

        return output.ToString();
    }
}
