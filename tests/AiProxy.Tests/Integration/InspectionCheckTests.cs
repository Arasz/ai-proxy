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
        public ProviderHost? Find(string host) => hosts.Contains(host) ? new ProviderHost(Dialect.Anthropic, HarnessOnly: false) : null;
        public IReadOnlyCollection<string> Hosts { get; } = hosts;
    }

    // Long enough for the proxy to create its first leaf certificate on a loaded CI runner.
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    // Tests that expect the check to give up use a short bound, so they do not wait a minute.
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    private static InspectionCheck Check(Uri proxy, string host, string dataDirectory, TimeSpan? timeout = null)
    {
        return new InspectionCheck(proxy, host, dataDirectory, timeout ?? Generous);
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
        var result = await Check(proxy.ProxyAddress, "elsewhere.test", proxy.DataDirectory, Short).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CheckStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task A_non_200_answer_to_the_tunnel_request_is_unknown_and_names_the_code()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer);
            await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\n\r\n"u8.ToArray());
        }, TestContext.Current.CancellationToken);
        using var directory = WithRoot();
        var result = await Check(new Uri($"http://127.0.0.1:{port}"), Host, directory.Path).RunAsync(TestContext.Current.CancellationToken);
        await server;
        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Contains("502", result.Detail);
        Assert.DoesNotContain("Bad Gateway", result.Detail);
    }

    private static TemporaryDirectory WithRoot()
    {
        var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using var root = RootCertificateAuthority.Create([Host], DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(directory.Path, "root-ca.pem"), root.ExportCertificatePem());
        return directory;
    }

    [Fact]
    public async Task A_closed_proxy_port_is_unknown()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var directory = WithRoot();
        var result = await Check(new Uri($"http://127.0.0.1:{port}"), Host, directory.Path).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Contains("did not finish", result.Detail);
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
