using System.Net;
using AiProxy.Hosting.Guard;
using AiProxy.Hosting.Marker;
using AiProxy.Hosting.Proxy;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace AiProxy.Hosting.Integration;

/// <summary>Runs the explicit HTTP(S) proxy and restores machine proxy settings on shutdown.</summary>
public sealed partial class TitaniumProxyService : IHostedService, IAsyncDisposable
{
    /// <summary>File name of the root CA pfx inside <see cref="ProxyRuntimeOptions.DataDirectory" />.</summary>
    public const string RootCertificateFileName = "root-ca.pfx";

    /// <summary>The public root certificate as PEM, for clients that trust a CA file (such as <c>NODE_EXTRA_CA_CERTS</c>).</summary>
    public const string RootCertificatePemFileName = "root-ca.pem";

    private readonly ISystemProxyGuardLauncher _guardLauncher;
    private readonly ProviderRequestInspector _inspector;
    private readonly ILogger<TitaniumProxyService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _markerWait;
    private readonly ProxyRuntimeOptions _options;
    private readonly IProviderHosts _providerHosts;

    private readonly ISystemProxyRegistrar _systemProxyRegistrar;
    private readonly TimeProvider _timeProvider;
    private readonly Func<HttpWebClient, bool> _upstreamClosed;
    private ExplicitProxyEndPoint? _endPoint;
    private ISystemProxyGuardSession? _guard;
    private SystemProxyMarker? _marker;
    private bool _started;
    private bool _systemProxySet;
    private volatile bool _upstreamStateUnreadable;

    /// <summary>Creates the proxy service without changing system settings until started.</summary>
    public TitaniumProxyService(
        ProxyRuntimeOptions options,
        ProviderRequestInspector inspector,
        IProviderHosts providerHosts,
        ILoggerFactory loggerFactory)
        : this(null, options, inspector, providerHosts, new ProcessSystemProxyGuardLauncher(), loggerFactory, TimeSpan.FromSeconds(3), TimeProvider.System)
    {
    }

    internal TitaniumProxyService(
        ISystemProxyRegistrar? systemProxyRegistrar,
        ProxyRuntimeOptions options,
        ProviderRequestInspector inspector,
        IProviderHosts providerHosts,
        ISystemProxyGuardLauncher guardLauncher,
        ILoggerFactory loggerFactory,
        TimeSpan markerWait,
        TimeProvider timeProvider,
        Func<HttpWebClient, bool>? upstreamClosed = null)
    {
        Server = new ProxyServer(false);
        _systemProxyRegistrar = systemProxyRegistrar ?? new TitaniumSystemProxyRegistrar(Server);
        _guardLauncher = guardLauncher;
        _markerWait = markerWait;
        _options = options;
        _inspector = inspector;
        _providerHosts = providerHosts;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TitaniumProxyService>();
        _upstreamClosed = upstreamClosed ?? UpstreamConnection.IsClosed;
        _timeProvider = timeProvider;
    }

    /// <summary>The listener address used by system proxy settings and local clients.</summary>
    public Uri Address => new($"http://{_options.ListenAddress}:{_endPoint?.Port ?? _options.Port}");

    internal IReadOnlyList<ProxyEndPoint> EndPoints => Server.ProxyEndPoints;

    internal ProxyServer Server { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Server.BeforeRequest -= BeforeRequestAsync;
        Server.BeforeResponse -= BeforeResponseAsync;
        Server.AfterResponse -= AfterResponseAsync;
        if (_endPoint is not null)
        {
            _endPoint.BeforeTunnelConnectRequest -= BeforeTunnelConnectRequestAsync;
        }

        ReleaseSystemProxy();
        if (_started)
        {
            Server.Stop();
            _started = false;
        }

        Server.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CertificateManager.SuppressInteractiveRootStoreMutations = !_options.TrustRootCertificate;
        Server.Logging.LoggerFactory = _loggerFactory;
        Server.EnableHttpInterception = true;
        Server.MaxBufferedBodyBytes = 8 * 1024 * 1024;
        Server.ViaHeaderPseudonym = "";
        Server.BeforeRequest += BeforeRequestAsync;
        Server.BeforeResponse += BeforeResponseAsync;
        Server.AfterResponse += AfterResponseAsync;
        Server.EnableRequestTimingCapture = _inspector.CapturesResponses;

        PrepareRootCertificate();
        if (_options.ConfigureSystemProxy)
        {
            _marker = await SystemProxyMarker.Acquire( _options.DataDirectory, _markerWait, _timeProvider);
        }

        _endPoint = new ExplicitProxyEndPoint(_options.ListenAddress, _options.Port);
        _endPoint.BeforeTunnelConnectRequest += BeforeTunnelConnectRequestAsync;
        Server.AddEndPoint(_endPoint);
        if (_options.TrustRootCertificate)
        {
            new TitaniumRootCertificate(Server.CertificateManager).EnsureTrusted();
        }

        try
        {
            Server.Start(false);
            _started = true;
            if (_options.ConfigureSystemProxy)
            {
                await SetSystemProxyAsync();
            }
        }
        catch
        {
            if (_started)
            {
                Server.Stop();
                _started = false;
            }

            _marker?.Dispose();
            _marker = null;
            throw;
        }

        Log.Started(_logger, Address);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        ReleaseSystemProxy();

        if (_started)
        {
            Server.Stop();
            _started = false;
        }

        return Task.CompletedTask;
    }

    private async Task SetSystemProxyAsync()
    {
        ArgumentNullException.ThrowIfNull(_marker);
        ArgumentNullException.ThrowIfNull(_endPoint);

        var marker = _marker;
        if (marker.PreviousRunDied)
        {
            Log.PreviousRunDied(_logger, marker.PreviousEndpoint);
            await _systemProxyRegistrar.ClearAsync();
        }

        marker.Write(new IPEndPoint(_options.ListenAddress, _endPoint.Port));
        try
        {
            _systemProxyRegistrar.Set(_endPoint);
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
            _guard = _guardLauncher.Launch(_options.DataDirectory, () => Log.GuardExited(_logger));
        }
        catch
        {
            ReleaseSystemProxy();
            throw;
        }
    }

    private void ReleaseSystemProxy()
    {
        var restored = true;
        if (_systemProxySet)
        {
            _systemProxySet = false;
            try
            {
                _systemProxyRegistrar.Restore();
            }
            catch (Exception exception)
            {
                restored = false;
                Log.RestoreFailed(_logger, exception);
            }
        }

        if (restored)
        {
            _marker?.Truncate();
            TryGuard(static guard => guard.ReportRestored());
        }

        TryGuard(static guard => guard.Dispose());
        _guard = null;
        if (restored)
        {
            _marker?.Delete();
        }
        else
        {
            _marker?.Dispose();
        }

        _marker = null;
    }

    private void TryGuard(Action<ISystemProxyGuardSession> action)
    {
        if (_guard is null)
        {
            return;
        }

        try
        {
            action(_guard);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.GuardUnreachable(_logger, exception);
        }
    }

    private void PrepareRootCertificate()
    {
        CreatePrivateDirectory(_options.DataDirectory);
        var certificates = Server.CertificateManager;
        certificates.PfxFilePath = Path.GetFullPath(Path.Combine(_options.DataDirectory, RootCertificateFileName));
        certificates.PfxPassword = _options.RootCertificatePassword ?? "";
        certificates.SaveFakeCertificates = false;
        if (!certificates.CreateRootCertificate())
        {
            throw new InvalidOperationException($"The ai-proxy root certificate could not be loaded or created in {_options.DataDirectory}.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(certificates.PfxFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        if (certificates.RootCertificate is { } root)
        {
            File.WriteAllText(Path.Combine(_options.DataDirectory, RootCertificatePemFileName), $"{root.ExportCertificatePem()}\n");
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

    private Task BeforeRequestAsync(object sender, SessionEventArgs e)
    {
        return _inspector.BeforeRequestAsync(sender, e);
    }

    private Task BeforeResponseAsync(object sender, SessionEventArgs e)
    {
        return _inspector.BeforeResponseAsync(sender, e);
    }

    private async Task AfterResponseAsync(object sender, SessionEventArgs e)
    {
        if (_inspector.CapturesResponses)
        {
            await _inspector.AfterResponseAsync(sender, e);
        }

        CloseClientAfterUpstreamClosed(e);
    }

    /// <summary>
    ///     Ends the client connection once the upstream one has closed. Titanium reads a chunked body
    ///     that stops between chunks as complete and keeps the client waiting for the next chunk.
    /// </summary>
    private void CloseClientAfterUpstreamClosed(SessionEventArgs e)
    {
        if (_upstreamStateUnreadable)
        {
            return;
        }

        bool closed;
        try
        {
            closed = _upstreamClosed(e.HttpClient);
        }
        catch (MissingMemberException exception)
        {
            _upstreamStateUnreadable = true;
            Log.UpstreamStateUnreadable(_logger, exception);
            return;
        }

        if (!closed)
        {
            return;
        }

        if (e.HttpClient.Response.IsChunked)
        {
            Log.UpstreamClosedMidStream(_logger, e.HttpClient.Request.RequestUri.Host);
        }

        e.TerminateSession();
    }

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

        [LoggerMessage(EventId = 1302, Level = LogLevel.Warning, Message = "A previous ai-proxy run ended with the system proxy set to {Endpoint}; clearing it before setting it again. A proxy configured before that run is not restored")]
        public static partial void PreviousRunDied(ILogger logger, string? endpoint);

        [LoggerMessage(EventId = 1303, Level = LogLevel.Warning, Message = "The system proxy guard could not be reached; it has probably ended")]
        public static partial void GuardUnreachable(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1304, Level = LogLevel.Warning, Message = "The system proxy guard ended while ai-proxy was still running; a forced kill would now leave the system proxy set until the next start")]
        public static partial void GuardExited(ILogger logger);

        [LoggerMessage(EventId = 1305, Level = LogLevel.Error, Message = "The system proxy could not be restored; the marker stays, so the guard or the next start clears it")]
        public static partial void RestoreFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1306, Level = LogLevel.Warning, Message = "The upstream {ProviderHost} closed its connection before the end of a chunked response; closing the client connection so the client sees the failure")]
        public static partial void UpstreamClosedMidStream(ILogger logger, string providerHost);

        [LoggerMessage(EventId = 1307, Level = LogLevel.Error, Message = "Titanium's upstream connection state could not be read; a client whose upstream drops a chunked response now waits until its own timeout")]
        public static partial void UpstreamStateUnreadable(ILogger logger, Exception exception);
    }
}
