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

    private const int WarnDays = 30;

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var file = new RootPemFile(dataDirectory);
        var where = $"{file.Path} (assumed from --data-directory)";
        try
        {
            var text = await file.ReadAsync(cancellationToken).ConfigureAwait(false);
            return text is null
                ? new CheckResult(Name, CheckStatus.Fail, $"{where} is larger than {RootPemFile.MaxBytes / 1024} KiB.", "Delete it and restart ai-proxy to publish a new root.")
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

    private CheckResult Judge(string text, string where)
    {
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return new CheckResult(Name, CheckStatus.Fail, $"{where} holds a private key; the published root must be the certificate alone.", "Delete the file and restart ai-proxy, then treat the old key as exposed.");
        }

        using var root = RootPemFile.Parse(text);
        if (root is null)
        {
            return new CheckResult(Name, CheckStatus.Fail, $"{where} holds no certificate.", "Delete the file and restart ai-proxy to publish a new root.");
        }

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
            : new CheckResult(Name, CheckStatus.Warn, $"The root at {where} is valid but this system does not trust it; clients that use NODE_EXTRA_CA_CERTS or CODEX_CA_CERTIFICATE are unaffected.", "Trust the root system-wide only if a client needs it.");
    }
}
