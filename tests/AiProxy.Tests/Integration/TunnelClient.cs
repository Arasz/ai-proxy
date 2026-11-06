using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AiProxy.Core.Gain;
using TitaniumProxyService = AiProxy.Hosting.Integration.TitaniumProxyService;

namespace AiProxy.Tests.Integration;

/// <summary>Raw CONNECT tunnels through the proxy, and the certificates a decrypted tunnel needs.</summary>
internal static class TunnelClient
{
    public const string ProxyAuthorization = "Proxy-Authorization";

    public static string Credential(string project) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{GainApi.ProxyUser}:{project}"));

    /// <summary>Accepts one connection and reads the first TLS record whole: its 5-byte header and the length it names.</summary>
    public static async Task<byte[]> ReadOriginAsync(TcpListener origin, CancellationToken token)
    {
        using var client = await origin.AcceptTcpClientAsync(token);
        var stream = client.GetStream();
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, token);
        var record = new byte[header.Length + ((header[3] << 8) | header[4])];
        header.CopyTo(record, 0);
        await stream.ReadExactlyAsync(record.AsMemory(header.Length), token);
        return record;
    }

    public static async ValueTask<Stream> ConnectThroughAsync(Uri proxy, DnsEndPoint target, string? credential, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(proxy.Host), proxy.Port), token);
        var stream = new NetworkStream(socket, true);
        var authorization = credential is null ? "" : $"{ProxyAuthorization}: {credential}\r\n";
        var connect = $"CONNECT {target.Host}:{target.Port} HTTP/1.1\r\nHost: {target.Host}:{target.Port}\r\n{authorization}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(connect), token);
        using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        headTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        token = headTimeout.Token;
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

    public static bool ChainsTo(X509Certificate2 root, X509Certificate2? certificate, SslPolicyErrors errors)
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

    public static X509Certificate2 LoadProxyRoot(string dataDirectory)
    {
        using var withKey = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(dataDirectory, TitaniumProxyService.RootCertificateFileName),
            File.ReadAllText(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificatePasswordFileName)));
        return X509CertificateLoader.LoadCertificate(withKey.RawData);
    }

    public static X509Certificate2 CreateUpstreamCertificate()
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
