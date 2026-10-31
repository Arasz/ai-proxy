using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AiProxy.Hosting.Integration;

/// <summary>Makes the inspection root: a CA whose name constraint permits only the provider domains and no IP address.</summary>
internal static class RootCertificateAuthority
{
    /// <summary>The root's common name, distinct from every other Titanium-based tool's root.</summary>
    public const string CommonName = "ai-proxy local inspection CA";

    private const string NameConstraintsOid = "2.5.29.30";
    private const string InvalidDomain = "invalid";

    /// <summary>Creates the root, with an exportable private key, constrained to the registrable parents of <paramref name="hosts" />.</summary>
    /// <param name="hosts">The provider hosts the proxy inspects.</param>
    /// <param name="now">The current time; the root is valid from a day before it for ten years.</param>
    public static X509Certificate2 Create(IEnumerable<string> hosts, DateTimeOffset now)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={CommonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(NameConstraints([.. hosts]));
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Whether a root carries a name constraint, which only roots made here do.</summary>
    public static bool IsConstrained(X509Certificate2 root)
    {
        return root.Extensions[NameConstraintsOid] is not null;
    }

    /// <summary>Whether a root carries the same permitted and excluded subtrees that <paramref name="hosts" /> would give a new root, in any order.</summary>
    /// <remarks>Compares subtrees as sets, so a root encoded in another host order (0.19.0 wrote catalog order) is kept.</remarks>
    public static bool PermitsExactly(X509Certificate2 root, IReadOnlyCollection<string> hosts)
    {
        return root.Extensions[NameConstraintsOid] is { } stored && Subtrees(stored.RawData).SetEquals(Subtrees(NameConstraints(hosts).RawData));
    }

    private static HashSet<string> Subtrees(byte[] nameConstraints)
    {
        var outer = new AsnReader(nameConstraints, AsnEncodingRules.DER).ReadSequence();
        var subtrees = new HashSet<string>(StringComparer.Ordinal);
        while (outer.HasData)
        {
            var list = outer.PeekTag();
            var entries = outer.ReadSequence(list);
            while (entries.HasData)
            {
                subtrees.Add($"{list.TagValue}:{Convert.ToHexString(entries.ReadEncodedValue().Span)}");
            }
        }

        return subtrees;
    }

    /// <summary>Encodes RFC 5280 4.2.1.10 NameConstraints: permitted DNS domains and IP addresses the hosts name, excluded the rest of each address family.</summary>
    /// <remarks>An address family no host names is excluded whole (0.0.0.0/0 or ::/0); one that a host names permits that address alone.</remarks>
    private static X509Extension NameConstraints(IReadOnlyCollection<string> hosts)
    {
        var addresses = hosts
            .Select(static host => IPAddress.TryParse(host.Trim('[', ']'), out var address) ? address : null)
            .OfType<IPAddress>()
            .Distinct()
            .ToArray();
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            {
                foreach (var domain in PermittedDomains(hosts))
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, domain, new Asn1Tag(TagClass.ContextSpecific, 2));
                    }
                }

                foreach (var address in addresses)
                {
                    WriteAddressSubtree(writer, address.GetAddressBytes(), true);
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        // OpenSSL checks a hostname-like subject CN as a DNS name when a leaf has no DNS SAN.
                        using (writer.PushSequence())
                        {
                            writer.WriteCharacterString(UniversalTagNumber.IA5String, address.ToString(), new Asn1Tag(TagClass.ContextSpecific, 2));
                        }
                    }
                }
            }

            var unnamedFamilies = new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 }
                .Where(family => addresses.All(address => address.AddressFamily != family))
                .ToArray();
            if (unnamedFamilies.Length > 0)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
                {
                    foreach (var family in unnamedFamilies)
                    {
                        WriteAddressSubtree(writer, new byte[family == AddressFamily.InterNetwork ? 4 : 16], false);
                    }
                }
            }
        }

        return new X509Extension(NameConstraintsOid, writer.Encode(), true);
    }

    private static void WriteAddressSubtree(AsnWriter writer, byte[] address, bool exact)
    {
        var mask = new byte[address.Length];
        Array.Fill(mask, exact ? (byte)0xFF : (byte)0x00);
        using (writer.PushSequence())
        {
            writer.WriteOctetString([.. address, .. mask], new Asn1Tag(TagClass.ContextSpecific, 7));
        }
    }

    /// <summary>The DNS domains the root permits: the last two labels of each host name, or <c>invalid</c> when there is none.</summary>
    public static IReadOnlyCollection<string> PermittedDomains(IEnumerable<string> hosts)
    {
        var domains = hosts
            .Select(static host => host.Trim('[', ']').TrimEnd('.'))
            .Where(static host => !IPAddress.TryParse(host, out _))
            .Select(static host => string.Join('.', host.Split('.').TakeLast(2)).ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return domains.Length == 0 ? [InvalidDomain] : domains;
    }
}
