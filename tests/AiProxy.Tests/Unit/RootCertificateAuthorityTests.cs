using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiProxy.Core.Providers;
using AiProxy.Hosting.Integration;

namespace AiProxy.Tests.Unit;

/// <summary>The inspection root signs leaves only for the provider domains it was made for.</summary>
public sealed class RootCertificateAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_permitted_domains_are_the_registrable_parents_of_the_provider_hosts()
    {
        var domains = RootCertificateAuthority.PermittedDomains(ProviderHostCatalog.Hosts);

        Assert.Equal(["anthropic.com", "googleapis.com", "openai.com", "openrouter.ai"], domains.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_root_is_a_named_certificate_authority_with_a_critical_name_constraint()
    {
        using var root = RootCertificateAuthority.Create(ProviderHostCatalog.Hosts, Now);

        Assert.Equal($"CN={RootCertificateAuthority.CommonName}", root.Subject);
        Assert.True(root.HasPrivateKey);
        var basic = Assert.Single(root.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.True(basic.CertificateAuthority);
        var usage = Assert.Single(root.Extensions.OfType<X509KeyUsageExtension>());
        Assert.True(usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign));
        var constraint = Assert.Single(root.Extensions.Cast<X509Extension>(), extension => extension.Oid?.Value == "2.5.29.30");
        Assert.True(constraint.Critical);
        Assert.True(RootCertificateAuthority.IsConstrained(root));
    }

    [Fact]
    public void A_leaf_for_a_provider_host_chain_builds_to_the_root()
    {
        using var root = RootCertificateAuthority.Create(ProviderHostCatalog.Hosts, Now);
        using var leaf = Leaf(root, "api.anthropic.com");

        using var chain = Chain(root);
        var built = chain.Build(leaf);

        Assert.True(built, string.Join(", ", chain.ChainStatus.Select(status => status.Status)));
    }

    [Fact]
    public void A_leaf_for_a_host_outside_the_provider_domains_fails_the_name_constraint()
    {
        using var root = RootCertificateAuthority.Create(ProviderHostCatalog.Hosts, Now);
        using var leaf = Leaf(root, "evil.example");

        using var chain = Chain(root);
        var built = chain.Build(leaf);

        Assert.False(built);
        Assert.Contains(ExpectedNameConstraintFailure(), chain.ChainStatus.Select(status => status.Status));
    }

    [Fact]
    public void A_leaf_for_an_ip_address_fails_the_name_constraint()
    {
        using var root = RootCertificateAuthority.Create(ProviderHostCatalog.Hosts, Now);
        using var leaf = Leaf(root, IPAddress.Parse("10.0.0.1"));

        using var chain = Chain(root);
        var built = chain.Build(leaf);

        Assert.False(built);
        Assert.Contains(ExpectedExcludedAddressFailure(), chain.ChainStatus.Select(status => status.Status));
    }

    [Fact]
    public void An_ip_address_a_host_source_names_is_permitted_alone()
    {
        string[] hosts = ["127.0.0.1", "::1", "localhost"];
        using var root = RootCertificateAuthority.Create(hosts, Now);
        using var loopback = Leaf(root, IPAddress.Loopback);
        using var other = Leaf(root, IPAddress.Parse("127.0.0.2"));

        using var allowed = Chain(root);
        using var refused = Chain(root);

        Assert.True(allowed.Build(loopback), string.Join(", ", allowed.ChainStatus.Select(status => status.Status)));
        Assert.False(refused.Build(other));
    }

    [Fact]
    public void A_host_source_naming_both_address_families_leaves_no_empty_excluded_list()
    {
        string[] hosts = ["127.0.0.1", "::1", "localhost"];
        using var root = RootCertificateAuthority.Create(hosts, Now);
        var constraint = Assert.Single(root.Extensions.Cast<X509Extension>(), extension => extension.Oid?.Value == "2.5.29.30");

        var outer = new AsnReader(constraint.RawData, AsnEncodingRules.DER).ReadSequence();
        outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));

        Assert.False(outer.HasData);
    }

    [Fact]
    public void A_root_without_a_name_constraint_is_not_constrained()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Titanium Root Certificate Authority", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var titanium = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));

        Assert.False(RootCertificateAuthority.IsConstrained(titanium));
    }

    /// <summary>The flag a chain reports for an address inside an excluded subtree.</summary>
    /// <remarks>Measured: Linux (OpenSSL) reports <see cref="X509ChainStatusFlags.HasExcludedNameConstraint" />, macOS
    /// <see cref="X509ChainStatusFlags.InvalidNameConstraints" />.</remarks>
    private static X509ChainStatusFlags ExpectedExcludedAddressFailure()
    {
        return OperatingSystem.IsMacOS() ? X509ChainStatusFlags.InvalidNameConstraints : X509ChainStatusFlags.HasExcludedNameConstraint;
    }

    /// <summary>The flag each chain engine reports for a name outside the constraint.</summary>
    /// <remarks>Linux (OpenSSL) and Windows report <see cref="X509ChainStatusFlags.HasNotPermittedNameConstraint" />. macOS's
    /// Security framework enforces the constraint but .NET maps its result to <see cref="X509ChainStatusFlags.InvalidNameConstraints" />.</remarks>
    private static X509ChainStatusFlags ExpectedNameConstraintFailure()
    {
        return OperatingSystem.IsMacOS() ? X509ChainStatusFlags.InvalidNameConstraints : X509ChainStatusFlags.HasNotPermittedNameConstraint;
    }

    private static X509Chain Chain(X509Certificate2 root)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = Now.UtcDateTime;
        return chain;
    }

    private static X509Certificate2 Leaf(X509Certificate2 root, string host)
    {
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        return Leaf(root, host, names);
    }

    private static X509Certificate2 Leaf(X509Certificate2 root, IPAddress address)
    {
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        return Leaf(root, address.ToString(), names);
    }

    private static X509Certificate2 Leaf(X509Certificate2 root, string subject, SubjectAlternativeNameBuilder names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        return request.Create(root, Now.AddDays(-1), Now.AddDays(30), RandomNumberGenerator.GetBytes(16));
    }
}
