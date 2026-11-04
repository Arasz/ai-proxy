using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiProxy.Core.Doctor;
using AiProxy.Core.Providers;
using AiProxy.Doctor;
using Microsoft.Extensions.Time.Testing;
using RootCertificateAuthority = AiProxy.Hosting.Integration.RootCertificateAuthority;

namespace AiProxy.Tests.Unit.Doctor;

public sealed class RootCheckTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Directory.CreateTempSubdirectory("doctor-root").FullName;

    private sealed class FixedTrust(bool trusted) : IRootTrust
    {
        public bool IsTrusted(X509Certificate2 root) => trusted;
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    private static X509Certificate2 Root(DateTimeOffset createdAt, IEnumerable<string>? hosts = null)
    {
        return RootCertificateAuthority.Create(hosts ?? ProviderHostCatalog.Hosts, createdAt);
    }

    private void WritePem(string text)
    {
        File.WriteAllText(Path.Combine(_directory, "root-ca.pem"), text);
    }

    private Task<CheckResult> Run(bool trusted = true)
    {
        return new RootCheck(_directory, new FakeTimeProvider(Now), new FixedTrust(trusted)).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_constrained_current_trusted_root_passes()
    {
        using var root = Root(Now);
        WritePem(root.ExportCertificatePem());
        var result = await Run();
        Assert.Equal(CheckStatus.Pass, result.Status);
        Assert.Contains(_directory, result.Detail);
        Assert.Contains("--data-directory", result.Detail);
    }

    [Fact]
    public async Task A_missing_file_fails()
    {
        var result = await Run();
        Assert.Equal(CheckStatus.Fail, result.Status);
    }

    [Fact]
    public async Task A_root_expiring_in_29_days_warns()
    {
        using var root = Root(Now.AddDays(29).AddYears(-10));
        WritePem(root.ExportCertificatePem());
        Assert.Equal(CheckStatus.Warn, (await Run()).Status);
    }

    [Fact]
    public async Task An_expired_root_fails()
    {
        using var root = Root(Now.AddYears(-11));
        WritePem(root.ExportCertificatePem());
        Assert.Equal(CheckStatus.Fail, (await Run()).Status);
    }

    [Fact]
    public async Task An_unconstrained_root_fails()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=plain", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var plain = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(5));
        WritePem(plain.ExportCertificatePem());
        Assert.Equal(CheckStatus.Fail, (await Run()).Status);
    }

    [Fact]
    public async Task A_root_constrained_to_other_hosts_fails()
    {
        using var root = Root(Now, ["example.com"]);
        WritePem(root.ExportCertificatePem());
        Assert.Equal(CheckStatus.Fail, (await Run()).Status);
    }

    [Fact]
    public async Task A_file_holding_a_private_key_fails()
    {
        using var root = Root(Now);
        WritePem(root.ExportCertificatePem() + "\n" + root.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
        var result = await Run();
        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.DoesNotContain("BEGIN", result.Detail + result.Fix);
    }

    [Fact]
    public async Task An_oversized_file_fails_without_being_parsed()
    {
        WritePem(new string('A', 70 * 1024));
        Assert.Equal(CheckStatus.Fail, (await Run()).Status);
    }

    [Fact]
    public async Task A_root_the_system_does_not_trust_only_warns()
    {
        using var root = Root(Now);
        WritePem(root.ExportCertificatePem());
        Assert.Equal(CheckStatus.Warn, (await Run(trusted: false)).Status);
    }

    [Fact]
    public async Task Checking_the_root_writes_nothing()
    {
        using var root = Root(Now);
        WritePem(root.ExportCertificatePem());
        var before = Snapshot();
        await Run();
        Assert.Equal(before, Snapshot());
    }

    private string[] Snapshot()
    {
        return [.. Directory.EnumerateFileSystemEntries(_directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => $"{path}:{(File.Exists(path) ? File.ReadAllText(path).GetHashCode() : 0)}")];
    }
}
