using System.Net;
using System.Net.Sockets;
using AiProxy.Core.Compression;
using AiProxy.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Tests.Integration;

public class TitaniumProxyServiceTests
{
    [Fact]
    public async Task System_proxy_registration_is_reversed_when_the_proxy_stops()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        var registrar = new RecordingSystemProxyRegistrar();
        await using var service = CreateService(registrar, port, directory.Path);

        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(registrar.EndPoint);
        Assert.Equal(port, registrar.EndPoint.Port);
        Assert.Equal(IPAddress.Loopback, registrar.EndPoint.IpAddress);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, registrar.RestoreCount);
    }

    [Fact]
    public async Task System_proxy_is_set_to_the_endpoint_registered_with_the_server()
    {
        using var directory = new TemporaryDirectory();
        var registrar = new RecordingSystemProxyRegistrar();
        await using var service = CreateService(registrar, FreePort(), directory.Path);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Contains(service.EndPoints, endPoint => ReferenceEquals(endPoint, registrar.EndPoint));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_system_proxy_set_is_not_restored_and_stops_the_listener()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        var registrar = new RecordingSystemProxyRegistrar { FailSet = true };
        await using var service = CreateService(registrar, port, directory.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, registrar.RestoreCount);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    private static TitaniumProxyService CreateService(ISystemProxyRegistrar registrar, int port, string dataDirectory) =>
        new(
            registrar,
            new ProxyRuntimeOptions(IPAddress.Loopback, port, ConfigureSystemProxy: true, TrustRootCertificate: false, dataDirectory),
            new ProviderRequestInspector(new PassThroughCompressionPipeline()),
            NullLogger<TitaniumProxyService>.Instance);

    private static int FreePort()
    {
        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();
        return port;
    }

    private sealed class RecordingSystemProxyRegistrar : ISystemProxyRegistrar
    {
        public bool FailSet { get; init; }

        public ExplicitProxyEndPoint? EndPoint { get; private set; }

        public int RestoreCount { get; private set; }

        public void Set(ExplicitProxyEndPoint endPoint)
        {
            if (FailSet)
            {
                throw new InvalidOperationException("networksetup failed");
            }

            EndPoint = endPoint;
        }

        public void Restore() => RestoreCount++;
    }
}
