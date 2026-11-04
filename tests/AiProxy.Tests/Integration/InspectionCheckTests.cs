using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using AiProxy.Core.Doctor;
using AiProxy.Core.Providers;
using AiProxy.Doctor;
using AiProxy.Hosting;
using RootCertificateAuthority = AiProxy.Hosting.Integration.RootCertificateAuthority;

namespace AiProxy.Tests.Integration;

public sealed class InspectionCheckTests
{
    private const string Host = "unreachable.test";

    private sealed class NamedHosts(params string[] hosts) : IProviderHosts
    {
        public Dialect? Find(string host) => hosts.Contains(host) ? Dialect.Anthropic : null;
        public IReadOnlyCollection<string> Hosts { get; } = hosts;
    }

    private static InspectionCheck Check(Uri proxy, string host, string dataDirectory)
    {
        return new InspectionCheck(proxy, host, dataDirectory, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_decrypted_tunnel_whose_leaf_chains_to_the_published_root_passes_even_with_the_upstream_unreachable()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new NamedHosts(Host));
        var result = await Check(proxy.ProxyAddress, Host, proxy.DataDirectory).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CheckStatus.Pass, result.Status);
    }

    [Fact]
    public async Task A_leaf_that_does_not_chain_to_the_given_root_fails()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new NamedHosts(Host));
        using var other = new TemporaryDirectory();
        using var stranger = RootCertificateAuthority.Create([Host], DateTimeOffset.UtcNow);
        Directory.CreateDirectory(other.Path);
        File.WriteAllText(Path.Combine(other.Path, "root-ca.pem"), stranger.ExportCertificatePem());
        var result = await Check(proxy.ProxyAddress, Host, other.Path).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CheckStatus.Fail, result.Status);
    }

    [Fact]
    public async Task A_tunnel_the_proxy_does_not_decrypt_to_an_unreachable_host_is_unknown_not_failed()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new NamedHosts(Host));
        var result = await Check(proxy.ProxyAddress, "elsewhere.test", proxy.DataDirectory).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CheckStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task A_closed_proxy_port_is_unknown()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var directory = new TemporaryDirectory();
        var result = await Check(new Uri($"http://127.0.0.1:{port}"), Host, directory.Path).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CheckStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task A_missing_root_file_is_unknown_because_the_root_check_reports_it()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new NamedHosts(Host));
        using var empty = new TemporaryDirectory();
        var result = await Check(proxy.ProxyAddress, Host, empty.Path).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CheckStatus.Unknown, result.Status);
    }
}
