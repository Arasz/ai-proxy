using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AiProxy.Core.Doctor;
using AiProxy.Core.Providers;
using AiProxy.Hosting.Integration;

namespace AiProxy.Doctor;

/// <summary>Reads the published root certificate and judges it: no private key, constrained to the provider hosts, in date, trusted.</summary>
/// <remarks>Read-only: it never creates, rotates or trusts the root.</remarks>
internal sealed class RootCheck(string dataDirectory, TimeProvider time, IRootTrust trust)
{
    public const string Name = "root";

    private const int MaxBytes = 64 * 1024;
    private const int WarnDays = 30;

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataDirectory, TitaniumProxyService.RootCertificatePemFileName);
        var where = $"{path} (assumed from --data-directory)";
        try
        {
            var text = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            return text is null
                ? new CheckResult(Name, CheckStatus.Fail, $"{where} is larger than {MaxBytes / 1024} KiB.", "Delete it and restart ai-proxy to publish a new root.")
                : Judge(text, where);
        }
        catch (FileNotFoundException)
        {
            return new CheckResult(Name, CheckStatus.Fail, $"No root certificate at {where}.", "Start ai-proxy once to create it, or pass --data-directory.");
        }
        catch (DirectoryNotFoundException)
        {
            return new CheckResult(Name, CheckStatus.Fail, $"No root certificate at {where}.", "Start ai-proxy once to create it, or pass --data-directory.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            return new CheckResult(Name, CheckStatus.Fail, $"The root certificate at {where} cannot be read: {exception.GetType().Name}.", "Delete it and restart ai-proxy to publish a new root.");
        }
    }

    private static async Task<string?> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[MaxBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return length > MaxBytes ? null : Encoding.UTF8.GetString(buffer, 0, length);
    }

    private CheckResult Judge(string text, string where)
    {
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return new CheckResult(Name, CheckStatus.Fail, $"{where} holds a private key; the published root must be the certificate alone.", "Delete the file and restart ai-proxy, then treat the old key as exposed.");
        }

        if (!PemEncoding.TryFind(text, out var fields) || !text.AsSpan(fields.Label).SequenceEqual("CERTIFICATE"))
        {
            return new CheckResult(Name, CheckStatus.Fail, $"{where} holds no certificate.", "Delete the file and restart ai-proxy to publish a new root.");
        }

        using var root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(text[fields.Base64Data].ToString()));
        if (!RootCertificateAuthority.IsConstrained(root) || !RootCertificateAuthority.PermitsExactly(root, [.. ProviderHostCatalog.Hosts]))
        {
            return new CheckResult(Name, CheckStatus.Fail, $"The root at {where} is not limited to the provider hosts.", "Delete the root files in the data directory and restart ai-proxy to create a constrained root.");
        }

        var now = time.GetUtcNow();
        if (now >= root.NotAfter || now < root.NotBefore)
        {
            return new CheckResult(Name, CheckStatus.Fail, $"The root at {where} is outside its validity period ({root.NotBefore:yyyy-MM-dd} to {root.NotAfter:yyyy-MM-dd}).", "Delete the root files in the data directory and restart ai-proxy.");
        }

        if (root.NotAfter - now < TimeSpan.FromDays(WarnDays))
        {
            return new CheckResult(Name, CheckStatus.Warn, $"The root at {where} expires on {root.NotAfter:yyyy-MM-dd}.", "Delete the root files in the data directory and restart ai-proxy before then.");
        }

        return trust.IsTrusted(root)
            ? new CheckResult(Name, CheckStatus.Pass, $"The root at {where} is constrained, valid until {root.NotAfter:yyyy-MM-dd}, and trusted by this system.")
            : new CheckResult(Name, CheckStatus.Warn, $"The root at {where} is valid but this system does not trust it; clients that use NODE_EXTRA_CA_CERTS are unaffected.", "Trust the root system-wide only if a client needs it.");
    }
}
