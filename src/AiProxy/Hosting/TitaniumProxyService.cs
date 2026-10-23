using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting;

/// <summary>Runs the explicit HTTP(S) proxy and restores machine proxy settings on shutdown.</summary>
public sealed partial class TitaniumProxyService : IHostedService, IAsyncDisposable
{
    /// <summary>File name of the root CA pfx inside <see cref="ProxyRuntimeOptions.DataDirectory"/>.</summary>
    public const string RootCertificateFileName = "root-ca.pfx";

    private readonly ProxyServer _server;
    private readonly ISystemProxyRegistrar _systemProxyRegistrar;
    private readonly ISystemProxyGuardLauncher _guardLauncher;
    private readonly ProxyRuntimeOptions _options;
    private readonly ProviderRequestInspector _inspector;
    private readonly IProviderHosts _providerHosts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TitaniumProxyService> _logger;
    private ExplicitProxyEndPoint? _endPoint;
    private bool _started;
    private bool _systemProxySet;
    private SystemProxyMarker? _marker;
    private ISystemProxyGuardSession? _guard;

    /// <summary>Creates the proxy service without changing system settings until started.</summary>
    public TitaniumProxyService(
        ProxyRuntimeOptions options,
        ProviderRequestInspector inspector,
        IProviderHosts providerHosts,
        ILoggerFactory loggerFactory)
        : this(null, options, inspector, providerHosts, new ProcessSystemProxyGuardLauncher(), loggerFactory)
    {
    }

    internal TitaniumProxyService(
        ISystemProxyRegistrar? systemProxyRegistrar,
        ProxyRuntimeOptions options,
        ProviderRequestInspector inspector,
        IProviderHosts providerHosts,
        ISystemProxyGuardLauncher guardLauncher,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(guardLauncher);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(providerHosts);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _server = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false, trustRootCertificateAsAdmin: false);
        _systemProxyRegistrar = systemProxyRegistrar ?? new TitaniumSystemProxyRegistrar(_server);
        _guardLauncher = guardLauncher;
        _options = options;
        _inspector = inspector;
        _providerHosts = providerHosts;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TitaniumProxyService>();
        Address = new Uri($"http://{options.ListenAddress}:{options.Port}");
    }

    /// <summary>The listener address used by system proxy settings and local clients.</summary>
    public Uri Address { get; }

    internal IReadOnlyList<ProxyEndPoint> EndPoints => _server.ProxyEndPoints;

    internal ProxyServer Server => _server;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Titanium.Web.Proxy.Network.CertificateManager.SuppressInteractiveRootStoreMutations = !_options.TrustRootCertificate;
        _server.Logging.LoggerFactory = _loggerFactory;
        _server.EnableHttpInterception = true;
        _server.MaxBufferedBodyBytes = 8 * 1024 * 1024;
        _server.ViaHeaderPseudonym = string.Empty;
        _server.BeforeRequest += BeforeRequestAsync;
        _server.BeforeResponse += BeforeResponseAsync;

        PrepareRootCertificate();
        if (_options.ConfigureSystemProxy)
        {
            _marker = SystemProxyMarker.Acquire(_options.DataDirectory);
        }

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

        try
        {
            _server.Start(changeSystemProxySettings: false);
            _started = true;
            if (_options.ConfigureSystemProxy)
            {
                SetSystemProxy();
            }
        }
        catch
        {
            if (_started)
            {
                _server.Stop();
                _started = false;
            }

            _marker?.Dispose();
            _marker = null;
            throw;
        }

        Log.Started(_logger, Address);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        ReleaseSystemProxy();

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
        ReleaseSystemProxy();
        if (_started)
        {
            _server.Stop();
            _started = false;
        }
        _server.Dispose();
        return ValueTask.CompletedTask;
    }

    private void SetSystemProxy()
    {
        var marker = _marker!;
        if (marker.PreviousRunDied)
        {
            Log.PreviousRunDied(_logger);
            _systemProxyRegistrar.Clear();
        }

        marker.Write(new IPEndPoint(_options.ListenAddress, _options.Port));
        try
        {
            _systemProxyRegistrar.Set(_endPoint!);
        }
        catch
        {
            marker.Delete();
            _marker = null;
            throw;
        }

        _systemProxySet = true;
        try
        {
            _guard = _guardLauncher.Launch(_options.DataDirectory);
        }
        catch
        {
            ReleaseSystemProxy();
            throw;
        }
    }

    private void ReleaseSystemProxy()
    {
        if (_systemProxySet)
        {
            _systemProxyRegistrar.Restore();
            _systemProxySet = false;
            _guard?.ReportRestored();
        }

        _guard?.Dispose();
        _guard = null;
        _marker?.Delete();
        _marker = null;
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

    private Task BeforeTunnelConnectRequestAsync(object sender, TunnelConnectSessionEventArgs e)
    {
        var authority = e.HttpClient.ConnectRequest?.Host;
        e.DecryptSsl = !string.IsNullOrWhiteSpace(authority) &&
                       Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var address) &&
                       _providerHosts.Find(address.Host) is not null;
        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "AI proxy listening at {ProxyAddress}")]
        public static partial void Started(ILogger logger, Uri proxyAddress);

        [LoggerMessage(EventId = 1302, Level = LogLevel.Warning, Message = "A previous ai-proxy run ended with the system proxy set; clearing it before setting it again. A proxy configured before that run is not restored")]
        public static partial void PreviousRunDied(ILogger logger);
    }
}
