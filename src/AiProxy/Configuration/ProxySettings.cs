using System.ComponentModel.DataAnnotations;

namespace AiProxy.Configuration;

/// <summary>What the operator configured, bound from the <c>Proxy</c> section (environment form <c>PROXY__PORT</c>).</summary>
public sealed record ProxySettings
{
    /// <summary>The configuration section the settings are read from.</summary>
    public const string SectionName = "Proxy";

    /// <summary>Binds and validates the <c>Proxy</c> section; an unknown key or an out-of-range value throws.</summary>
    public static ProxySettings Load(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var settings = configuration.GetSection(SectionName).Get<ProxySettings>(static binder => binder.ErrorOnUnknownConfiguration = true) ?? new ProxySettings();
        Validator.ValidateObject(settings, new ValidationContext(settings), true);
        return settings;
    }

    /// <summary>The loopback port the listener binds; unset keeps the default.</summary>
    [Range(0, 65535, ErrorMessage = "Proxy:Port must be a port number from 0 to 65535.")]
    public int? Port { get; init; }

    /// <summary>The directory where the private state (root CA, control socket, leases) is stored; unset keeps the runtime default.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Whether to configure the machine proxy; unset keeps the runtime default.</summary>
    public bool? ConfigureSystemProxy { get; init; }

    /// <summary>Whether to trust the root certificate; unset keeps the runtime default.</summary>
    public bool? TrustRootCertificate { get; init; }

    /// <summary>Password protecting the root CA pfx file; unset or empty keeps the runtime option.</summary>
    public string? RootCertificatePassword { get; init; }

    /// <summary>Whether every provider request body is stored.</summary>
    public bool Capture { get; init; }

    /// <summary>The live bytes of the capture file above which capture stops; unset keeps 1 GB.</summary>
    [Range(1, long.MaxValue, ErrorMessage = "Proxy:CaptureMaxBytes must be a positive number of bytes.")]
    public long? CaptureMaxBytes { get; init; }

    /// <summary>The compression stage settings.</summary>
    public CompressionSettings Compression { get; init; } = new();

    /// <summary>The recoverable compression settings.</summary>
    public CcrSettings Ccr { get; init; } = new();
}
