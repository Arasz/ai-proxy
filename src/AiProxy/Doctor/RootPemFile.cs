using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AiProxy.Hosting.Integration;

namespace AiProxy.Doctor;

/// <summary>The published root certificate file in a data directory, read with a size bound and never written.</summary>
internal sealed class RootPemFile(string dataDirectory)
{
    public const int MaxBytes = 64 * 1024;

    public string Path { get; } = System.IO.Path.Combine(dataDirectory, TitaniumProxyService.RootCertificatePemFileName);

    /// <summary>The file text, or null when it is larger than <see cref="MaxBytes" />.</summary>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

    /// <summary>The first certificate block in <paramref name="text" />, or null when there is none.</summary>
    public static X509Certificate2? Parse(string text)
    {
        if (!PemEncoding.TryFind(text, out var fields) || !text.AsSpan(fields.Label).SequenceEqual("CERTIFICATE"))
        {
            return null;
        }

        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(text[fields.Base64Data].ToString()));
    }
}
