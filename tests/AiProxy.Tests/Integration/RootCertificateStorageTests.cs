using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiProxy.Ccr;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
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

    private static string StoredPassword(string dataDirectory)
    {
        return File.ReadAllText(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificatePasswordFileName));
    }

    private static X509Certificate2 LoadRoot(string dataDirectory, string password)
    {
        return X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificateFileName), password);
    }

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

    private static async Task StartAndStopAsync(string dataDirectory, string? password, RecordingLoggerProvider? log = null)
    {
        await using var service = CreateService(dataDirectory, password, false, log: log);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);
    }

    private static TitaniumProxyService CreateService(
        string dataDirectory,
        string? password,
        bool trust,
        RecordingTrust? rootTrust = null,
        RecordingLoggerProvider? log = null)
    {

        ILoggerFactory loggerFactory = log is null ? NullLoggerFactory.Instance : new LoggerFactory([log]);
        return new TitaniumProxyService(
            null,
            new ProxyRuntimeOptions(IPAddress.Loopback, 0, false, trust, dataDirectory)
            {
                RootCertificatePassword = password
            },
            new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), new NoRequestCapture(), new CcrOff(), NullLogger<ProviderRequestInspector>.Instance, TimeProvider.System, new NoGainLedger()),
            new CanonicalProviderHosts(),
            new ProcessSystemProxyGuardLauncher(),
            loggerFactory,
            TimeSpan.FromSeconds(3),
            TimeProvider.System,
            rootCertificate: _ => rootTrust ?? throw new InvalidOperationException("This test never trusts a certificate."));
    }

    /// <summary>Records trust calls instead of touching a certificate store.</summary>
    private sealed class RecordingTrust : IRootCertificate
    {
        private bool _trusted;

        public List<string> Calls { get; } = [];

        public void Load()
        {
            Calls.Add("load");
        }

        public bool IsTrusted()
        {
            Calls.Add("check");
            return _trusted;
        }

        public void Trust()
        {
            Calls.Add("trust");
            _trusted = true;
        }

        public void Untrust(string thumbprint)
        {
            Calls.Add($"untrust {thumbprint}");
        }
    }
}
