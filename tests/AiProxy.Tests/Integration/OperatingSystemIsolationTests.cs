using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using AiProxy.Core.Compression;
using AiProxy.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests.Integration;

public sealed class OperatingSystemIsolationTests
{
    private const string RootName = "Titanium Root Certificate Authority";

    [Fact]
    public async Task Starting_without_trust_opt_in_leaves_the_user_certificate_stores_untouched()
    {
        using var directory = new TemporaryDirectory();
        var before = CountRootCertificates();
        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();

        await using (var service = new TitaniumProxyService(
            new ProxyRuntimeOptions(IPAddress.Loopback, port, ConfigureSystemProxy: false, TrustRootCertificate: false, directory.Path),
            new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), compressionEnabled: false, NullLogger<ProviderRequestInspector>.Instance),
            new CanonicalProviderHosts(),
            NullLoggerFactory.Instance))
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(before, CountRootCertificates());
    }

    private static (int My, int Root) CountRootCertificates() =>
        (Count(StoreName.My), Count(StoreName.Root));

    private static int Count(StoreName storeName)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return 0;
        }

        return store.Certificates.Find(X509FindType.FindBySubjectName, RootName, validOnly: false).Count;
    }
}
