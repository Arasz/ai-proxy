using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using AiProxy.Core.Providers;

namespace AiProxy.Hosting;

/// <summary>Runs the explicit HTTP(S) proxy and restores machine proxy settings on shutdown.</summary>
public sealed partial class TitaniumProxyService : IHostedService, IAsyncDisposable
{
    /// <summary>File name of the root CA pfx inside <see cref="ProxyRuntimeOptions.DataDirectory"/>.</summary>
    public const string RootCertificateFileName = "root-ca.pfx";

    private readonly ProxyServer _server;
    private readonly ISystemProxyRegistrar _systemProxyRegistrar;
    private readonly ProxyRuntimeOptions _options;
    private readonly ProviderRequestInspector _inspector;
    private readonly ILogger<TitaniumProxyService> _logger;
    private ExplicitProxyEndPoint? _endPoint;
    private bool _started;
    private bool _systemProxySet;

    /// <summary>Creates the proxy service without changing system settings until started.</summary>
    public TitaniumProxyService(
        ProxyRuntimeOptions options,
        ProviderRequestInspector inspector,
        ILogger<TitaniumProxyService> logger)
        : this(null, options, inspector, logger)
    {
    }

    internal TitaniumProxyService(
        ISystemProxyRegistrar? systemProxyRegistrar,
        ProxyRuntimeOptions options,
        ProviderRequestInspector inspector,
        ILogger<TitaniumProxyService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(logger);
        _server = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false, trustRootCertificateAsAdmin: false);
        _systemProxyRegistrar = systemProxyRegistrar ?? new TitaniumSystemProxyRegistrar(_server);
        _options = options;
        _inspector = inspector;
        _logger = logger;
        Address = new Uri($"http://{options.ListenAddress}:{options.Port}");
    }

    /// <summary>The listener address used by system proxy settings and local clients.</summary>
    public Uri Address { get; }

    internal IReadOnlyList<ProxyEndPoint> EndPoints => _server.ProxyEndPoints;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Titanium.Web.Proxy.Network.CertificateManager.SuppressInteractiveRootStoreMutations = !_options.TrustRootCertificate;
        _server.EnableHttpInterception = true;
        _server.MaxBufferedBodyBytes = 8 * 1024 * 1024;
        _server.ViaHeaderPseudonym = string.Empty;
        _server.BeforeRequest += BeforeRequestAsync;
        _server.BeforeResponse += BeforeResponseAsync;

        PrepareRootCertificate();

        _endPoint = new ExplicitProxyEndPoint(_options.ListenAddress, _options.Port, decryptSsl: true);
        _endPoint.BeforeTunnelConnectRequest += BeforeTunnelConnectRequestAsync;
        _server.AddEndPoint(_endPoint);
        if (_options.TrustRootCertificate)
        {
            _server.CertificateManager.EnsureRootCertificate(userTrustRootCertificate: true, machineTrustRootCertificate: false);
            if (!_server.CertificateManager.IsRootCertificateUserTrusted())
            {
                throw new InvalidOperationException(
                    "The ai-proxy HTTPS inspection certificate is not trusted. Trust the ai-proxy root certificate for this user, then start ai-proxy again.");
            }
        }

        _server.Start(changeSystemProxySettings: false);
        _started = true;
        if (_options.ConfigureSystemProxy)
        {
            try
            {
                _systemProxyRegistrar.Set(_endPoint);
                _systemProxySet = true;
            }
            catch
            {
                _server.Stop();
                _started = false;
                throw;
            }
        }

        Log.Started(_logger, Address);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_systemProxySet)
        {
            _systemProxyRegistrar.Restore();
            _systemProxySet = false;
        }

        if (_started)
        {
            _server.Stop();
            _started = false;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _server.BeforeRequest -= BeforeRequestAsync;
        _server.BeforeResponse -= BeforeResponseAsync;
        if (_endPoint is not null)
        {
            _endPoint.BeforeTunnelConnectRequest -= BeforeTunnelConnectRequestAsync;
        }
        if (_systemProxySet)
        {
            _systemProxyRegistrar.Restore();
            _systemProxySet = false;
        }
        if (_started)
        {
            _server.Stop();
            _started = false;
        }
        _server.Dispose();
        return ValueTask.CompletedTask;
    }

    private void PrepareRootCertificate()
    {
        CreatePrivateDirectory(_options.DataDirectory);
        var certificates = _server.CertificateManager;
        certificates.PfxFilePath = Path.GetFullPath(Path.Combine(_options.DataDirectory, RootCertificateFileName));
        certificates.PfxPassword = _options.RootCertificatePassword ?? string.Empty;
        certificates.SaveFakeCertificates = false;
        if (!certificates.CreateRootCertificate())
        {
            throw new InvalidOperationException($"The ai-proxy root certificate could not be loaded or created in {_options.DataDirectory}.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(certificates.PfxFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        const UnixFileMode userOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, userOnly);
        File.SetUnixFileMode(path, userOnly);
    }

    private Task BeforeRequestAsync(object sender, SessionEventArgs e) => _inspector.BeforeRequestAsync(sender, e);

    private Task BeforeResponseAsync(object sender, SessionEventArgs e) => _inspector.BeforeResponseAsync(sender, e);

    private static Task BeforeTunnelConnectRequestAsync(object sender, TunnelConnectSessionEventArgs e)
    {
        var host = e.HttpClient.ConnectRequest?.Host;
        e.DecryptSsl = host is not null && ProviderHostCatalog.ShouldInspectHttpsAuthority(host);
        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "AI proxy listening at {ProxyAddress}")]
        public static partial void Started(ILogger logger, Uri proxyAddress);
    }
}
