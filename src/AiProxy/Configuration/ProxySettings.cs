using System.ComponentModel.DataAnnotations;

namespace AiProxy.Configuration;

/// <summary>What the operator configured, bound from the <c>Proxy</c> section (environment form <c>PROXY__PORT</c>).</summary>
public sealed record ProxySettings
{
    /// <summary>The configuration section the settings are read from.</summary>
    public const string SectionName = "Proxy";

    /// <summary>The loopback port the listener binds; unset keeps the default.</summary>
    [Range(1, 65535, ErrorMessage = "Proxy:Port must be a port number from 1 to 65535.")]
    public int? Port { get; init; }

    /// <summary>Password protecting the root CA pfx file; unset or empty keeps the runtime option.</summary>
    public string? RootCertificatePassword { get; init; }

    /// <summary>Whether every provider request body is stored.</summary>
    public bool Capture { get; init; }

    /// <summary>The compression stage settings.</summary>
    public CompressionSettings Compression { get; init; } = new();

    /// <summary>The recoverable compression settings.</summary>
    public CcrSettings Ccr { get; init; } = new();

    /// <summary>The directory where private state (root CA, control socket, leases) is stored.</summary>
    public string? DataDirectory { get; init; }
}