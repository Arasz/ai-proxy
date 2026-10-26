using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AiProxy.Ccr;
using AiProxy.Core.Capture;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests.Integration;

/// <summary>HTTPS to a provider host is decrypted with the proxy's own root, trusted only inside this process.</summary>
public sealed class HttpsInspectionTests
{
    private const string Body = """{"model":"m","messages":[{"role":"user","content":"hello"}]}""";

    [Fact]
    public async Task A_provider_https_request_is_decrypted_and_compressed()
    {
        using var directory = new TemporaryDirectory();
        using var upstreamCertificate = CreateUpstreamCertificate();
        await using var upstream = await FakeUpstream.StartAsync(upstreamCertificate);
        var hosts = new LoopbackProviderHosts(Dialect.Anthropic);
        await using var proxy = new TitaniumProxyService(
            new ProxyRuntimeOptions(IPAddress.Loopback, FreePort(), false, false, directory.Path),
            new ProviderRequestInspector(new MarkerCompressionPipeline(), hosts, new NoRequestCapture(), new CcrOff(), NullLogger<ProviderRequestInspector>.Instance, TimeProvider.System, new NoGainLedger()),
            hosts,
            NullLoggerFactory.Instance);
        proxy.Server.ServerCertificateValidationCallback += (_, e) =>
        {
            e.IsValid = e.Certificate is not null &&
                        e.Certificate.GetCertHashString() == upstreamCertificate.GetCertHashString();
            return Task.CompletedTask;
        };
        await proxy.StartAsync(TestContext.Current.CancellationToken);
        using var proxyRoot = LoadProxyRoot(directory.Path);
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(proxy.Address),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => ChainsTo(proxyRoot, certificate, errors)
        };
        using var client = new HttpClient(handler);

        using var response = await client.PostAsync(
            new Uri(upstream.BaseAddress, "/v1/messages"),
            new StringContent(Body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.Single(upstream.Requests);
        Assert.Equal(Body + MarkerCompressionPipeline.Marker, received.Body);
        await proxy.StopAsync(CancellationToken.None);
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
            null);
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

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}