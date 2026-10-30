using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Network;

namespace AiProxy.Hosting.Integration;

/// <summary>The proxy's root certificate as far as trusting it goes.</summary>
internal interface IRootCertificate
{
    /// <summary>Loads the certificate, or creates it on the first run, without touching its trust.</summary>
    void Load();

    /// <summary>Whether the current user trusts the certificate.</summary>
    bool IsTrusted();

    /// <summary>Trusts the certificate for the current user; on macOS this asks the user to approve.</summary>
    void Trust();

    /// <summary>Stops trusting the certificate with this thumbprint for the current user, and no other.</summary>
    void Untrust(string thumbprint);
}

/// <summary>Titanium's certificate manager, trusted only through <see cref="Trust" />, so its own later checks never trust again.</summary>
/// <param name="certificates">The proxy server's certificate manager.</param>
internal sealed class TitaniumRootCertificate(CertificateManager certificates) : IRootCertificate
{
    /// <inheritdoc />
    public void Load()
    {
        certificates.EnsureRootCertificate();
    }

    /// <inheritdoc />
    public bool IsTrusted()
    {
        return certificates.IsRootCertificateUserTrusted();
    }

    /// <inheritdoc />
    public void Trust()
    {
        certificates.TrustRootCertificate();
    }

    /// <inheritdoc />
    /// <remarks>Matches by thumbprint only: Titanium's own untrust helpers also match by common name, which would remove other
    /// Titanium-based tools' roots. On macOS the login-keychain trust Titanium added with <c>security add-trusted-cert</c> is
    /// removed with <c>security delete-certificate -Z</c>, as Titanium itself does. Linux NSS databases are not cleaned.</remarks>
    public void Untrust(string thumbprint)
    {
        certificates.RemoveCertificateByThumbprint(StoreName.Root, StoreLocation.CurrentUser, thumbprint);
        certificates.RemoveCertificateByThumbprint(StoreName.My, StoreLocation.CurrentUser, thumbprint);
        if (OperatingSystem.IsMacOS() && !CertificateManager.AreInteractiveRootStoreMutationsSuppressed)
        {
            var start = new ProcessStartInfo("/usr/bin/security") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("delete-certificate");
            start.ArgumentList.Add("-Z");
            start.ArgumentList.Add(thumbprint);
            start.ArgumentList.Add("-t");
            using var process = Process.Start(start);
            if (process is not null)
            {
                var errors = process.StandardError.ReadToEndAsync();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                errors.Wait();
            }
        }
    }
}

/// <summary>Trusting the root certificate once per start.</summary>
internal static class RootCertificateExtensions
{
    extension(IRootCertificate root)
    {
        /// <summary>Loads the certificate and trusts it only when the user does not trust it yet; throws when it stays untrusted.</summary>
        public void EnsureTrusted()
        {
            root.Load();
            if (root.IsTrusted())
            {
                return;
            }

            root.Trust();
            if (!root.IsTrusted())
            {
                throw new InvalidOperationException(
                    "The ai-proxy HTTPS inspection certificate is not trusted. Trust the ai-proxy root certificate for this user, then start ai-proxy again.");
            }
        }
    }
}