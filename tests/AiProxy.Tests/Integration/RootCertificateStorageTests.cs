using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiProxy.Ccr;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using AiProxy.Hosting.Guard;
using AiProxy.Hosting.Integration;
using AiProxy.Hosting.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Web.Proxy.Network;

namespace AiProxy.Tests.Integration;

public sealed class RootCertificateStorageTests
{
    private const string OldRootName = "CN=Titanium Root Certificate Authority";

    [Fact]
    public async Task The_root_certificate_is_written_to_the_configured_directory_and_readable_only_by_the_user()
    {
        using var directory = new TemporaryDirectory();

        await StartAndStopAsync(directory.Path, null);

        var pfx = Path.Combine(directory.Path, TitaniumProxyService.RootCertificateFileName);
        Assert.True(File.Exists(pfx));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "crts")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pfx));
        }
    }

    [Fact]
    public async Task The_public_root_certificate_is_written_as_pem_for_clients_that_take_a_ca_file()
    {
        using var directory = new TemporaryDirectory();

        await StartAndStopAsync(directory.Path, null);

        var pem = await File.ReadAllTextAsync(Path.Combine(directory.Path, TitaniumProxyService.RootCertificatePemFileName), TestContext.Current.CancellationToken);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", pem, StringComparison.Ordinal);
        using var fromPem = X509Certificate2.CreateFromPem(pem);
        using var fromPfx = LoadRoot(directory.Path, StoredPassword(directory.Path));
        Assert.Equal(fromPfx.Thumbprint, fromPem.Thumbprint);
        Assert.False(fromPem.HasPrivateKey);
    }

    [Fact]
    public async Task The_stored_password_opens_the_root_certificate_and_an_empty_one_does_not()
    {
        using var directory = new TemporaryDirectory();

        await StartAndStopAsync(directory.Path, null);

        var passwordFile = Path.Combine(directory.Path, TitaniumProxyService.RootCertificatePasswordFileName);
        Assert.True(File.Exists(passwordFile));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(passwordFile));
        }

        Assert.ThrowsAny<CryptographicException>(() => LoadRoot(directory.Path, ""));
        using var root = LoadRoot(directory.Path, StoredPassword(directory.Path));
        Assert.True(root.HasPrivateKey);
    }

    [Fact]
    public async Task Titanium_signs_with_the_constrained_root_on_disk_and_never_a_wildcard()
    {
        using var directory = new TemporaryDirectory();
        await using var service = CreateService(directory.Path, null, false);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        using var onDisk = LoadRoot(directory.Path, StoredPassword(directory.Path));
        Assert.Equal($"CN={RootCertificateAuthority.CommonName}", onDisk.Subject);
        Assert.True(RootCertificateAuthority.IsConstrained(onDisk));
        Assert.Equal(onDisk.Thumbprint, service.Server.CertificateManager.RootCertificate?.Thumbprint);
        Assert.True(service.Server.CertificateManager.DisableWildCardCertificates);
    }

    [Fact]
    public async Task A_data_directory_holding_a_constrained_root_keeps_it()
    {
        using var directory = new TemporaryDirectory();
        await StartAndStopAsync(directory.Path, null);
        var password = StoredPassword(directory.Path);
        string first;
        using (var root = LoadRoot(directory.Path, password))
        {
            first = root.Thumbprint;
        }

        var log = new RecordingLoggerProvider();
        await StartAndStopAsync(directory.Path, null, log);

        Assert.Equal(password, StoredPassword(directory.Path));
        using var second = LoadRoot(directory.Path, password);
        Assert.Equal(first, second.Thumbprint);
        Assert.DoesNotContain(log.Entries, entry => entry.EventId == 1308);
    }

    [Fact]
    public async Task A_constrained_root_made_for_fewer_hosts_is_rotated_when_a_host_is_added()
    {
        using var directory = new TemporaryDirectory();
        await StartAndStopAsync(directory.Path, null, hosts: ["api.alpha.test"]);
        var password = StoredPassword(directory.Path);
        string oldThumbprint;
        using (var before = LoadRoot(directory.Path, password))
        {
            oldThumbprint = before.Thumbprint;
        }

        var trust = new RecordingTrust();
        var log = new RecordingLoggerProvider();
        var suppressed = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            await using var service = CreateService(directory.Path, null, true, trust, log, ["api.alpha.test", "api.beta.test"]);
            await service.StartAsync(TestContext.Current.CancellationToken);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = suppressed;
        }

        Assert.Equal(password, StoredPassword(directory.Path));
        using var after = LoadRoot(directory.Path, password);
        Assert.NotEqual(oldThumbprint, after.Thumbprint);
        Assert.True(RootCertificateAuthority.PermitsExactly(after, ["api.alpha.test", "api.beta.test"]));
        Assert.Equal(["load", "check", "trust", "check", $"untrust {oldThumbprint}"], trust.Calls);
        Assert.Single(log.Entries, entry => entry.EventId == 1308);
    }

    [Fact]
    public async Task A_root_that_names_registrable_parents_is_replaced_once_and_only_its_thumbprint_is_untrusted()
    {
        using var directory = new TemporaryDirectory();
        var oldThumbprint = WriteParentRoot(directory.Path);
        var trust = new RecordingTrust();
        var suppressed = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            await using var service = CreateService(directory.Path, null, true, trust);
            await service.StartAsync(TestContext.Current.CancellationToken);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = suppressed;
        }

        Assert.Equal(["load", "check", "trust", "check", $"untrust {oldThumbprint}"], trust.Calls);
        using var after = LoadRoot(directory.Path, StoredPassword(directory.Path));
        Assert.NotEqual(oldThumbprint, after.Thumbprint);
        Assert.True(RootCertificateAuthority.PermitsExactly(after, [.. ProviderHostCatalog.Hosts]));
    }

    [Fact]
    public async Task A_declined_trust_prompt_does_not_lose_the_old_thumbprint_and_the_next_start_untrusts_it()
    {
        using var directory = new TemporaryDirectory();
        var oldThumbprint = WriteParentRoot(directory.Path);
        var suppressed = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            var declined = new RecordingTrust { DuringTrust = () => throw new InvalidOperationException("declined") };
            await using (var first = CreateService(directory.Path, null, true, declined))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => first.StartAsync(TestContext.Current.CancellationToken));
            }

            var accepted = new RecordingTrust();
            await using var second = CreateService(directory.Path, null, true, accepted);
            await second.StartAsync(TestContext.Current.CancellationToken);
            await second.StopAsync(CancellationToken.None);

            Assert.Contains($"untrust {oldThumbprint}", accepted.Calls);
            Assert.Empty(Directory.EnumerateFiles(directory.Path, "root-ca.replaced*"));

            var third = new RecordingTrust();
            await using var again = CreateService(directory.Path, null, true, third);
            await again.StartAsync(TestContext.Current.CancellationToken);
            await again.StopAsync(CancellationToken.None);
            Assert.DoesNotContain(third.Calls, call => call.StartsWith("untrust", StringComparison.Ordinal));
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = suppressed;
        }
    }

    [Fact]
    public async Task A_failed_untrust_keeps_the_record_for_the_next_start()
    {
        using var directory = new TemporaryDirectory();
        var oldThumbprint = WriteParentRoot(directory.Path);
        var suppressed = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            await using (var first = CreateService(directory.Path, null, true, new RecordingTrust { UntrustFailure = new IOException("no tool") }))
            {
                await first.StartAsync(TestContext.Current.CancellationToken);
                await first.StopAsync(CancellationToken.None);
            }

            var retry = new RecordingTrust();
            await using var second = CreateService(directory.Path, null, true, retry);
            await second.StartAsync(TestContext.Current.CancellationToken);
            await second.StopAsync(CancellationToken.None);

            Assert.Contains($"untrust {oldThumbprint}", retry.Calls);
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = suppressed;
        }
    }

    [Fact]
    public async Task A_root_made_for_the_current_hosts_is_kept_by_a_trusting_start_and_nothing_is_untrusted()
    {
        using var directory = new TemporaryDirectory();
        await StartAndStopAsync(directory.Path, null);
        var password = StoredPassword(directory.Path);
        string first;
        using (var root = LoadRoot(directory.Path, password))
        {
            first = root.Thumbprint;
        }

        var trust = new RecordingTrust();
        var suppressed = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            await using var service = CreateService(directory.Path, null, true, trust);
            await service.StartAsync(TestContext.Current.CancellationToken);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = suppressed;
        }

        Assert.DoesNotContain(trust.Calls, call => call.StartsWith("untrust", StringComparison.Ordinal));
        using var second = LoadRoot(directory.Path, password);
        Assert.Equal(first, second.Thumbprint);
    }

    [Fact]
    public async Task After_a_rotation_a_leaf_for_a_provider_host_chains_to_the_new_root_and_not_the_old_one()
    {
        using var directory = new TemporaryDirectory();
        var oldThumbprint = WriteParentRoot(directory.Path);
        var suppressed = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            await using var service = CreateService(directory.Path, null, true, new RecordingTrust());
            await service.StartAsync(TestContext.Current.CancellationToken);

            using var leaf = await service.Server.CertificateManager.CreateServerCertificate("api.anthropic.com");

            var current = service.Server.CertificateManager.RootCertificate;
            Assert.NotNull(leaf);
            Assert.NotEqual(oldThumbprint, current?.Thumbprint);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(current!);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.True(chain.Build(leaf), string.Join(", ", chain.ChainStatus.Select(status => status.Status)));
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = suppressed;
        }
    }

    [Fact]
    public async Task The_same_hosts_in_another_order_keep_the_root_and_untrust_nothing()
    {
        using var directory = new TemporaryDirectory();
        await StartAndStopAsync(directory.Path, null, hosts: ["api.alpha.test", "api.beta.test", "10.0.0.2", "10.0.0.1"]);
        var password = StoredPassword(directory.Path);
        string first;
        using (var root = LoadRoot(directory.Path, password))
        {
            first = root.Thumbprint;
        }

        var log = new RecordingLoggerProvider();
        await StartAndStopAsync(directory.Path, null, log, ["10.0.0.1", "api.beta.test", "10.0.0.2", "api.alpha.test"]);

        using var second = LoadRoot(directory.Path, password);
        Assert.Equal(first, second.Thumbprint);
        Assert.DoesNotContain(log.Entries, entry => entry.EventId == 1308);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("from config")]
    public async Task A_start_against_an_old_titanium_root_replaces_it_and_untrusts_only_that_thumbprint(string? configuredPassword)
    {
        using var directory = new TemporaryDirectory();
        var oldThumbprint = WriteOldRoot(directory.Path, configuredPassword ?? "");
        var trust = new RecordingTrust();
        var log = new RecordingLoggerProvider();
        var suppressed = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            await using var service = CreateService(directory.Path, configuredPassword, true, trust, log);
            await service.StartAsync(TestContext.Current.CancellationToken);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = suppressed;
        }

        var pem = await File.ReadAllTextAsync(Path.Combine(directory.Path, TitaniumProxyService.RootCertificatePemFileName), TestContext.Current.CancellationToken);
        using var fromPem = X509Certificate2.CreateFromPem(pem);
        Assert.NotEqual(oldThumbprint, fromPem.Thumbprint);
        Assert.True(RootCertificateAuthority.IsConstrained(fromPem));
        Assert.Equal(["load", "check", "trust", "check", $"untrust {oldThumbprint}"], trust.Calls);
        var replaced = Assert.Single(log.Entries, entry => entry.EventId == 1308);
        Assert.Contains("restart every ai-proxy harness session", replaced.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_certificate_store_failure_during_trust_is_a_one_line_warning()
    {
        using var directory = new TemporaryDirectory();
        var trust = new RecordingTrust();
        var log = new RecordingLoggerProvider();
        await using var service = CreateService(directory.Path, null, true, trust, log);
        trust.DuringTrust = () => service.Server.Logger.LogError(new InvalidOperationException("Failed to open Root", new UnauthorizedAccessException("Access is denied.")), "{Context}", "Certificate operation failed");

        await StartAndStopTrustingAsync(service);

        var warning = Assert.Single(log.Entries, entry => entry.EventId == 1310);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Null(warning.Exception);
        Assert.Contains("Failed to open Root: Access is denied.", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Any_other_titanium_error_stays_an_error()
    {
        using var directory = new TemporaryDirectory();
        var trust = new RecordingTrust();
        var log = new RecordingLoggerProvider();
        await using var service = CreateService(directory.Path, null, true, trust, log);
        trust.DuringTrust = () => service.Server.Logger.LogError(new InvalidOperationException("broken"), "{Context}", "Listener failed");

        await StartAndStopTrustingAsync(service);

        var error = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.NotNull(error.Exception);
        Assert.DoesNotContain(log.Entries, entry => entry.EventId == 1310);
    }

    [Fact]
    public async Task A_replaced_root_that_cannot_be_untrusted_warns_and_the_start_goes_on()
    {
        using var directory = new TemporaryDirectory();
        var oldThumbprint = WriteOldRoot(directory.Path, "");
        var trust = new RecordingTrust { UntrustFailure = new IOException("security is missing") };
        var log = new RecordingLoggerProvider();
        await using var service = CreateService(directory.Path, null, true, trust, log);

        await StartAndStopTrustingAsync(service);

        var warning = Assert.Single(log.Entries, entry => entry.EventId == 1311);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(oldThumbprint, warning.Message, StringComparison.Ordinal);
        Assert.Contains("security is missing", warning.Message, StringComparison.Ordinal);
        Assert.Single(log.Entries, entry => entry.EventId == 1308);
    }

    [Fact]
    public async Task A_root_certificate_no_known_password_opens_stops_the_start_and_stays_on_disk()
    {
        using var directory = new TemporaryDirectory();
        WriteOldRoot(directory.Path, "a password nobody configured");
        var before = await File.ReadAllBytesAsync(Path.Combine(directory.Path, TitaniumProxyService.RootCertificateFileName), TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAndStopAsync(directory.Path, null));

        Assert.Contains(TitaniumProxyService.RootCertificateFileName, failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory.Path, TitaniumProxyService.RootCertificateFileName), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_existing_directory_with_open_permissions_is_tightened()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only.");
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory.Path, (UnixFileMode)0b111_101_101);

            await StartAndStopAsync(directory.Path, null);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory.Path));
        }
    }

    [Fact]
    public async Task The_root_certificate_is_protected_with_the_configured_password()
    {
        using var directory = new TemporaryDirectory();

        await StartAndStopAsync(directory.Path, "correct horse");

        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(directory.Path, TitaniumProxyService.RootCertificateFileName),
            TestContext.Current.CancellationToken);
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(bytes, ""));
        using var root = X509CertificateLoader.LoadPkcs12(bytes, "correct horse");
        Assert.True(root.HasPrivateKey);
        Assert.False(File.Exists(Path.Combine(directory.Path, TitaniumProxyService.RootCertificatePasswordFileName)));
    }

    [Fact]
    public async Task The_root_certificate_password_is_read_from_configuration()
    {
        byte[] bytes;
        await using (var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
                     {
                         ["Proxy:RootCertificatePassword"] = "from config"
                     }))
        {
            bytes = await File.ReadAllBytesAsync(
                Path.Combine(proxy.DataDirectory, TitaniumProxyService.RootCertificateFileName),
                TestContext.Current.CancellationToken);
        }

        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(bytes, ""));
        using var root = X509CertificateLoader.LoadPkcs12(bytes, "from config");
        Assert.True(root.HasPrivateKey);
    }

    private static string StoredPassword(string dataDirectory) => File.ReadAllText(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificatePasswordFileName));

    private static X509Certificate2 LoadRoot(string dataDirectory, string password) => X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificateFileName), password);

    /// <summary>Writes the root Titanium used to make: its generic name, no name constraint, the given password.</summary>
    private static string WriteOldRoot(string dataDirectory, string password)
    {
        Directory.CreateDirectory(dataDirectory);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(OldRootName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var old = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificateFileName), old.Export(X509ContentType.Pfx, password));
        return old.Thumbprint;
    }

    /// <summary>Writes the 0.34.0-style root: constrained to the registrable parent of each catalog host, opened by the stored password.</summary>
    private static string WriteParentRoot(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        string[] parents = [.. ProviderHostCatalog.Hosts.Select(static host => string.Join('.', host.Split('.').TakeLast(2))).Distinct()];
        using var old = RootCertificateAuthority.Create(parents, DateTimeOffset.UtcNow);
        const string password = "stored-test-password";
        File.WriteAllText(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificatePasswordFileName), password);
        File.WriteAllBytes(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificateFileName), old.Export(X509ContentType.Pfx, password));
        return old.Thumbprint;
    }

    private static async Task StartAndStopAsync(string dataDirectory, string? password, RecordingLoggerProvider? log = null, string[]? hosts = null)
    {
        await using var service = CreateService(dataDirectory, password, false, log: log, hosts: hosts);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>Runs a trusting start, which clears Titanium's process-wide suppression, and puts the suppression back.</summary>
    private static async Task StartAndStopTrustingAsync(TitaniumProxyService service)
    {
        var suppressed = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = suppressed;
        }
    }

    private static TitaniumProxyService CreateService(
        string dataDirectory,
        string? password,
        bool trust,
        RecordingTrust? rootTrust = null,
        RecordingLoggerProvider? log = null,
        string[]? hosts = null)
    {
        IProviderHosts providerHosts = hosts is null ? new CanonicalProviderHosts() : new FixedHosts(hosts);
        ILoggerFactory loggerFactory = log is null ? NullLoggerFactory.Instance : new LoggerFactory([log]);
        return TestTitaniumProxyService.Create(
            new ProxyRuntimeOptions(IPAddress.Loopback, 0, false, trust, dataDirectory)
            {
                RootCertificatePassword = password
            },
            new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), new NoRequestCapture(), new CcrOff(), NullLogger<ProviderRequestInspector>.Instance, TimeProvider.System, new NoGainLedger()),
            providerHosts,
            new ProcessSystemProxyGuardLauncher(),
            loggerFactory,
            TimeSpan.FromSeconds(3),
            TimeProvider.System,
            rootCertificate: _ => rootTrust ?? throw new InvalidOperationException("This test never trusts a certificate."));
    }

    private sealed class FixedHosts(string[] hosts) : IProviderHosts
    {
        public IReadOnlyCollection<string> Hosts => hosts;

        public ProviderHostMatch Find(string host) => NoneProviderHost.Instance;
    }

    /// <summary>Records trust calls instead of touching a certificate store.</summary>
    private sealed class RecordingTrust : IRootCertificate
    {
        private bool _trusted;

        public List<string> Calls { get; } = [];

        public Action? DuringTrust { get; set; }

        public Exception? UntrustFailure { get; init; }

        public void Load() => Calls.Add("load");

        public bool IsTrusted()
        {
            Calls.Add("check");
            return _trusted;
        }

        public void Trust()
        {
            Calls.Add("trust");
            DuringTrust?.Invoke();
            _trusted = true;
        }

        public void Untrust(string thumbprint)
        {
            Calls.Add($"untrust {thumbprint}");
            if (UntrustFailure is not null)
            {
                throw UntrustFailure;
            }
        }
    }
}
