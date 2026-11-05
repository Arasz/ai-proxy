using System.Net;
using System.Text;

namespace AiProxy.Hosting.Proxy;

/// <summary>Controls the local listener, its reversible machine proxy integration, and where its private state lives.</summary>
/// <param name="DataDirectory">User-only directory holding the root CA key.</param>
public sealed record ProxyRuntimeOptions(
    IPAddress ListenAddress,
    int Port,
    bool ConfigureSystemProxy,
    bool TrustRootCertificate,
    string DataDirectory)
{
    public const int DefaultPort = 8080;
    private const string DefaultDataDirectoryName = ".ai-proxy";
    private static readonly IPAddress DefaultListenAddress = IPAddress.Loopback;

    /// <summary>The per-user directory used by the installed application: <c>~/.ai-proxy</c>.</summary>
    /// <remarks>On Windows the <c>USERPROFILE</c> variable wins over the shell folder, as <c>HOME</c> does elsewhere, so a process given another profile never reaches the user's instance.</remarks>
    public static string DefaultDataDirectory { get; } = Path.Combine(UserProfile(), DefaultDataDirectoryName);

    private static string UserProfile()
    {
        var variable = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("USERPROFILE") : null;
        return string.IsNullOrWhiteSpace(variable) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : variable;
    }

    /// <summary>The loopback endpoint used by the installed application.</summary>
    public static ProxyRuntimeOptions Default { get; } = new(DefaultListenAddress, DefaultPort, true, true, DefaultDataDirectory);

    /// <summary>Password protecting the root CA pfx file; null or empty leaves the file unencrypted.</summary>
    public string? RootCertificatePassword { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ListenAddress = {ListenAddress}, Port = {Port}, ConfigureSystemProxy = {ConfigureSystemProxy}, ");
        builder.Append($"TrustRootCertificate = {TrustRootCertificate}, DataDirectory = {DataDirectory}");
        return true;
    }
}
