using AiProxy.Core.Capture;
using AiProxy.Ccr;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiProxy.Core.Compression;
using AiProxy.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests.Integration;

public sealed class RootCertificateStorageTests
{
    [Fact]
    public async Task The_root_certificate_is_written_to_the_configured_directory_and_readable_only_by_the_user()
    {
        using var directory = new TemporaryDirectory();

        await StartAndStopAsync(directory.Path, password: null);

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

        await StartAndStopAsync(directory.Path, password: null);

        var pem = await File.ReadAllTextAsync(Path.Combine(directory.Path, TitaniumProxyService.RootCertificatePemFileName), TestContext.Current.CancellationToken);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", pem, StringComparison.Ordinal);
        using var fromPem = X509Certificate2.CreateFromPem(pem);
        using var fromPfx = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(directory.Path, TitaniumProxyService.RootCertificateFileName), "");
        Assert.Equal(fromPfx.Thumbprint, fromPem.Thumbprint);
        Assert.False(fromPem.HasPrivateKey);
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

            await StartAndStopAsync(directory.Path, password: null);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory.Path));
        }
    }

    [Fact]
    public async Task The_root_certificate_is_protected_with_the_configured_password()
    {
        using var directory = new TemporaryDirectory();

        await StartAndStopAsync(directory.Path, password: "correct horse");

        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(directory.Path, TitaniumProxyService.RootCertificateFileName),
            TestContext.Current.CancellationToken);
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(bytes, ""));
        using var root = X509CertificateLoader.LoadPkcs12(bytes, "correct horse");
        Assert.True(root.HasPrivateKey);
    }

    [Fact]
    public async Task The_root_certificate_password_is_read_from_configuration()
    {
        byte[] bytes;
        await using (var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["Proxy:RootCertificatePassword"] = "from config",
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

    private static async Task StartAndStopAsync(string dataDirectory, string? password)
    {
        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();

        await using var service = new TitaniumProxyService(
            new ProxyRuntimeOptions(IPAddress.Loopback, port, ConfigureSystemProxy: false, TrustRootCertificate: false, dataDirectory)
            {
                RootCertificatePassword = password,
            },
            new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), new NoRequestCapture(), new CcrOff(), NullLogger<ProviderRequestInspector>.Instance, TimeProvider.System, new NoGainLedger()),
            new CanonicalProviderHosts(),
            NullLoggerFactory.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);
    }
}
