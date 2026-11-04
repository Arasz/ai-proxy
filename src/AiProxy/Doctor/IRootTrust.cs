using System.Security.Cryptography.X509Certificates;

namespace AiProxy.Doctor;

/// <summary>Whether this machine trusts a root certificate.</summary>
internal interface IRootTrust
{
    bool IsTrusted(X509Certificate2 root);
}

/// <summary>Asks the operating system's trust store, without revocation lookups or downloads.</summary>
internal sealed class SystemRootTrust : IRootTrust
{
    public bool IsTrusted(X509Certificate2 root)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.System;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        return chain.Build(root);
    }
}
