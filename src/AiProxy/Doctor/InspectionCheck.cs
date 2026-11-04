using System.Security.Authentication;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AiProxy.Core.Doctor;
using AiProxy.Hosting.Integration;

namespace AiProxy.Doctor;

/// <summary>Opens a CONNECT tunnel through the proxy and checks that the host's certificate chains to the published root; sends no HTTP request.</summary>
internal sealed class InspectionCheck(Uri proxy, string host, string dataDirectory, TimeSpan timeout)
{
    public const string Name = "inspection";

    private const int HttpsPort = 443;
    private const int MaxHeaderBytes = 8 * 1024;
    private const uint EndOfHeaders = 0x0D0A0D0A;

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        X509Certificate2? root;
        try
        {
            var text = await new RootPemFile(dataDirectory).ReadAsync(cancellationToken).ConfigureAwait(false);
            root = text is null ? null : RootPemFile.Parse(text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or CryptographicException)
        {
            root = null;
        }

        if (root is null)
        {
            return Unknown("the published root could not be read; see the root check.");
        }

        using (root)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                return await ProbeAsync(root, limit.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return Unknown($"the TLS handshake for {host} did not finish ({exception.GetType().Name}); this can mean the proxy could not reach the upstream.");
            }
        }
    }

    private async Task<CheckResult> ProbeAsync(X509Certificate2 root, CancellationToken cancellationToken)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(proxy.Host, proxy.Port, cancellationToken).ConfigureAwait(false);
        var stream = socket.GetStream();
        var request = Encoding.ASCII.GetBytes($"CONNECT {host}:{HttpsPort} HTTP/1.1\r\nHost: {host}:{HttpsPort}\r\n\r\n");
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var code = await ReadStatusCodeAsync(stream, cancellationToken).ConfigureAwait(false);
        if (code != "200")
        {
            return Unknown($"the proxy answered the tunnel request for {host} with status {code ?? "(none)"}.");
        }

        var chained = false;
        var presented = false;
        using var tls = new SslStream(stream, false, (_, certificate, _, _) =>
        {
            presented = certificate is not null;
            chained = certificate is not null && ChainsTo(root, certificate);
            return true;
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken).ConfigureAwait(false);
        return presented && chained
            ? new CheckResult(Name, CheckStatus.Pass, $"{host} is decrypted by the proxy; its certificate chains to the published root.")
            : new CheckResult(Name, CheckStatus.Fail, $"{host} is not decrypted by this proxy, or its certificate does not chain to the published root.", "Restart ai-proxy, and check that --data-directory is the proxy's own.");
    }

    private static bool ChainsTo(X509Certificate2 root, X509Certificate certificate)
    {
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        return chain.Build(leaf);
    }

    private static async Task<string?> ReadStatusCodeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new List<byte>();
        var one = new byte[1];
        var lastFour = 0u;
        while (head.Count < MaxHeaderBytes && lastFour != EndOfHeaders)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                break;
            }

            head.Add(one[0]);
            lastFour = (lastFour << 8) | one[0];
        }

        var parts = Encoding.ASCII.GetString([.. head]).Split("\r\n")[0].Split(' ', 3);
        return parts.Length >= 2 && parts[0].StartsWith("HTTP/", StringComparison.Ordinal) && parts[1].Length == 3 && parts[1].All(char.IsAsciiDigit) ? parts[1] : null;
    }

    private static CheckResult Unknown(string reason)
    {
        return new CheckResult(Name, CheckStatus.Unknown, $"Could not tell: {reason}");
    }
}
