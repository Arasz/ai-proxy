using Titanium.Web.Proxy.Network;

namespace AiProxy.Hosting;

/// <summary>The proxy's root certificate as far as trusting it goes.</summary>
internal interface IRootCertificate
{
    /// <summary>Loads the certificate, or creates it on the first run, without touching its trust.</summary>
    void Load();

    /// <summary>Whether the current user trusts the certificate.</summary>
    bool IsTrusted();

    /// <summary>Trusts the certificate for the current user; on macOS this asks the user to approve.</summary>
    void Trust();
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