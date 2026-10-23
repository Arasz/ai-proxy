using System.Net;
using System.Net.Sockets;
using AiProxy.Core.Compression;
using AiProxy.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests.Integration;

public class TitaniumProxyServiceTests
{
    [Fact]
    public async Task System_proxy_registration_is_reversed_when_the_proxy_stops()
    {
        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();

        using var directory = new TemporaryDirectory();
        var registrar = new RecordingSystemProxyRegistrar();
        await using var service = new TitaniumProxyService(
            registrar,
            new ProxyRuntimeOptions(IPAddress.Loopback, port, ConfigureSystemProxy: true, TrustRootCertificate: false, directory.Path),
            new ProviderRequestInspector(new PassThroughCompressionPipeline()),
            NullLogger<TitaniumProxyService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(port, registrar.SetPort);
        Assert.Equal(IPAddress.Loopback, registrar.SetAddress);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, registrar.RestoreCount);
    }

    private sealed class RecordingSystemProxyRegistrar : ISystemProxyRegistrar
    {
        public IPAddress? SetAddress { get; private set; }

        public int? SetPort { get; private set; }

        public int RestoreCount { get; private set; }

        public void Set(IPAddress address, int port)
        {
            SetAddress = address;
            SetPort = port;
        }

        public void Restore() => RestoreCount++;
    }
}
