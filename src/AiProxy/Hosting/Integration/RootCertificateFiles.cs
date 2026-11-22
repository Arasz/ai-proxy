using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AiProxy.Hosting.Integration;

/// <summary>The inspection root as loaded for this start.</summary>
/// <param name="Root">The constrained root, with an exportable private key.</param>
/// <param name="ReplacedThumbprint">The thumbprint of the old root this start replaced, or null when none was.</param>
/// <param name="Files">The files the root came from; they also record replaced roots still to be untrusted.</param>
internal sealed record RootCertificateLoad(X509Certificate2 Root, string? ReplacedThumbprint, RootCertificateFiles Files);

/// <summary>The inspection root's pfx, its generated password and its public pem in the private data directory.</summary>
/// <param name="dataDirectory">The user-only directory holding the files.</param>
/// <param name="configuredPassword">The <c>Proxy:RootCertificatePassword</c> setting; when set it wins over a generated password.</param>
/// <param name="hosts">The provider hosts a new root is constrained to.</param>
/// <param name="time">The clock a new root's validity starts from.</param>
internal sealed class RootCertificateFiles(string dataDirectory, string? configuredPassword, IReadOnlyCollection<string> hosts, TimeProvider time)
{
    private const UnixFileMode UserOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private string PfxPath => Path.Combine(dataDirectory, TitaniumProxyService.RootCertificateFileName);

    private string PasswordPath => Path.Combine(dataDirectory, TitaniumProxyService.RootCertificatePasswordFileName);

    /// <summary>Keeps a root already on disk that is constrained to exactly <c>hosts</c>; otherwise creates one, replacing an old root only once its thumbprint is read.</summary>
    /// <exception cref="InvalidOperationException">The pfx on disk opens with no password ai-proxy knows; it is left untouched.</exception>
    public RootCertificateLoad Load()
    {
        var password = configuredPassword ?? StoredPassword();
        string? replaced = null;
        if (File.Exists(PfxPath))
        {
            var bytes = File.ReadAllBytes(PfxPath);
            var current = TryOpen(bytes, password);
            if (current is not null && RootCertificateAuthority.IsConstrained(current) && RootCertificateAuthority.PermitsExactly(current, hosts) && current.NotAfter > time.GetLocalNow())
            {
                WritePem(current);
                return new RootCertificateLoad(current, null, this);
            }

            using var old = current ?? TryOpen(bytes, "") ?? throw new InvalidOperationException(
                $"The ai-proxy root certificate {PfxPath} opens with neither the configured or stored password nor an empty one. Move it aside to create a new root.");
            replaced = old.Thumbprint;
            Record(replaced);
        }

        var root = RootCertificateAuthority.Create(hosts, time.GetUtcNow());
        WritePrivate(PfxPath, root.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password));
        WritePem(root);
        return new RootCertificateLoad(root, replaced, this);
    }

    private string ReplacedPath => Path.Combine(dataDirectory, TitaniumProxyService.ReplacedRootsFileName);

    /// <summary>Thumbprints of replaced roots that may still be trusted, one per line.</summary>
    public IReadOnlyList<string> PendingUntrust() =>
        File.Exists(ReplacedPath)
            ? [.. File.ReadAllLines(ReplacedPath).Select(static line => line.Trim()).Where(static line => line.Length > 0).Distinct(StringComparer.Ordinal)]
            : [];

    /// <summary>Drops a thumbprint from the record once its root is no longer trusted.</summary>
    public void Forget(string thumbprint)
    {
        var remaining = PendingUntrust().Where(pending => pending != thumbprint).ToArray();
        if (remaining.Length == 0)
        {
            File.Delete(ReplacedPath);
            return;
        }

        WritePrivate(ReplacedPath, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', remaining)));
    }

    private void Record(string thumbprint)
    {
        var all = PendingUntrust().Append(thumbprint).Distinct(StringComparer.Ordinal);
        WritePrivate(ReplacedPath, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', all)));
    }

    private string StoredPassword()
    {
        if (File.Exists(PasswordPath))
        {
            return File.ReadAllText(PasswordPath);
        }

        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        WritePrivate(PasswordPath, System.Text.Encoding.UTF8.GetBytes(password));
        return password;
    }

    private void WritePem(X509Certificate2 root) => File.WriteAllText(Path.Combine(dataDirectory, TitaniumProxyService.RootCertificatePemFileName), $"{root.ExportCertificatePem()}\n");

    private static X509Certificate2? TryOpen(byte[] pfx, string password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Writes a file readable only by the user, through a temporary file so a reader never sees half of it.</summary>
    private static void WritePrivate(string path, byte[] contents)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Path.GetRandomFileName());
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UserOnly;
        }

        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(contents);
        }

        File.Move(temporary, path, true);
    }
}
