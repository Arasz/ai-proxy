using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AiProxy.Core.Gain;
using AiProxy.Core.Providers;
using Microsoft.Extensions.DependencyInjection;
using TitaniumProxyService = AiProxy.Hosting.Integration.TitaniumProxyService;

namespace AiProxy.Tests.Integration;

/// <summary>A proxy credential names the project of provider traffic and never reaches an origin.</summary>
public sealed class ProxyCredentialAttributionTests
{
    private const string ProxyAuthorization = "Proxy-Authorization";
    private const string Body = """{"messages":[]}""";

    private static string Credential(string project) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{GainApi.ProxyUser}:{project}"));

    private static async Task<string?> OnlyProjectAsync(ProxyHost proxy)
    {
        using var api = new HttpClient { BaseAddress = proxy.BaseAddress };
        var report = await api.GetFromJsonAsync<GainReport>("/gain", TestContext.Current.CancellationToken);
        return Assert.Single(report!.History).Project;
    }

    private static HttpRequestMessage Post(Uri uri, string? credential, string? project = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        if (credential is not null)
        {
            request.Headers.TryAddWithoutValidation(ProxyAuthorization, credential);
        }

        if (project is not null)
        {
            request.Headers.Add(GainApi.ProjectHeader, project);
        }

        return request;
    }

    [Fact]
    public async Task A_plain_request_credential_names_the_project_and_is_not_forwarded()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new LoopbackProviderHosts(Dialect.Anthropic));
        using var client = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true });

        using var response = await client.SendAsync(Post(new Uri(upstream.BaseAddress, "/v1/messages"), Credential("/project/b")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/project/b", await OnlyProjectAsync(proxy));
        Assert.DoesNotContain(Assert.Single(upstream.Requests).Headers.Keys, key => key.Equals(ProxyAuthorization, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_tunnel_credential_names_the_project_of_the_decrypted_request()
    {
        using var upstreamCertificate = CreateUpstreamCertificate();
        await using var upstream = await FakeUpstream.StartAsync(upstreamCertificate);
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new LoopbackProviderHosts(Dialect.Anthropic));
        proxy.Services.GetRequiredService<TitaniumProxyService>().Server.ServerCertificateValidationCallback += (_, e) =>
        {
            e.IsValid = e.Certificate is not null && e.Certificate.GetCertHashString() == upstreamCertificate.GetCertHashString();
            return Task.CompletedTask;
        };
        using var proxyRoot = LoadProxyRoot(proxy.DataDirectory);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = (context, token) => ConnectThroughAsync(proxy.ProxyAddress, context.DnsEndPoint, Credential("/project/c"), token),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, certificate, _, errors) => ChainsTo(proxyRoot, certificate as X509Certificate2, errors) }
        };
        using var client = new HttpClient(handler);

        using var response = await client.SendAsync(Post(new Uri(upstream.BaseAddress, "/v1/messages"), null), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/project/c", await OnlyProjectAsync(proxy));
    }

    [Fact]
    public async Task Traffic_without_a_credential_is_served_not_challenged()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new LoopbackProviderHosts(Dialect.Anthropic));
        using var client = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true });

        using var response = await client.SendAsync(Post(new Uri(upstream.BaseAddress, "/v1/messages"), null), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await OnlyProjectAsync(proxy));
    }

    [Fact]
    public async Task The_project_header_wins_over_a_credential()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new LoopbackProviderHosts(Dialect.Anthropic));
        using var client = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true });

        using var response = await client.SendAsync(Post(new Uri(upstream.BaseAddress, "/v1/messages"), Credential("/from/credential"), "/from/header"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/from/header", await OnlyProjectAsync(proxy));
    }

    [Fact]
    public async Task A_request_to_another_host_does_not_forward_the_credential()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true });

        using var response = await client.SendAsync(Post(new Uri(upstream.BaseAddress, "/other"), Credential("/project/e")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Assert.Single(upstream.Requests).Headers.Keys, key => key.Equals(ProxyAuthorization, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_tunnel_to_an_undecrypted_host_is_opened_and_the_origin_never_sees_the_credential()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var received = ReadOriginAsync(origin);
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        var target = new DnsEndPoint("127.0.0.1", ((IPEndPoint)origin.LocalEndpoint).Port);

        await using (var tunnel = await ConnectThroughAsync(proxy.ProxyAddress, target, Credential("/project/f"), TestContext.Current.CancellationToken))
        {
            using var tls = new SslStream(tunnel);
            var handshake = tls.AuthenticateAsClientAsync("origin.test");
            var bytes = Convert.FromHexString(await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(0x16, bytes[0]);
            Assert.DoesNotContain(ProxyAuthorization, Encoding.ASCII.GetString(bytes), StringComparison.OrdinalIgnoreCase);
            tunnel.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(() => handshake);
        }
    }

    private static async Task<string> ReadOriginAsync(TcpListener origin)
    {
        using var client = await origin.AcceptTcpClientAsync();
        var buffer = new byte[4096];
        var read = await client.GetStream().ReadAsync(buffer);
        return Convert.ToHexString(buffer, 0, read);
    }

    private static async ValueTask<Stream> ConnectThroughAsync(Uri proxy, DnsEndPoint target, string credential, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(proxy.Host), proxy.Port), token);
        var stream = new NetworkStream(socket, true);
        var connect = $"CONNECT {target.Host}:{target.Port} HTTP/1.1\r\nHost: {target.Host}:{target.Port}\r\n{ProxyAuthorization}: {credential}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(connect), token);
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, token) == 0)
            {
                throw new IOException("The proxy closed the tunnel request.");
            }

            head.Append((char)one[0]);
        }

        Assert.StartsWith("HTTP/1.1 200", head.ToString(), StringComparison.Ordinal);
        return stream;
    }

    private static bool ChainsTo(X509Certificate2 root, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    private static X509Certificate2 LoadProxyRoot(string dataDirectory)
    {
        using var withKey = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(dataDirectory, TitaniumProxyService.RootCertificateFileName),
            File.ReadAllText(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificatePasswordFileName)));
        return X509CertificateLoader.LoadCertificate(withKey.RawData);
    }

    private static X509Certificate2 CreateUpstreamCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}
