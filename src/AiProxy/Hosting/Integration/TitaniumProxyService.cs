using System.ComponentModel;
using System.Net;
using System.Net.Mime;
using System.Security.Cryptography;
using AiProxy.Core.Gain;
using AiProxy.Hosting.Guard;
using AiProxy.Hosting.Marker;
using AiProxy.Hosting.Proxy;
using Microsoft.Net.Http.Headers;
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

    /// <summary>File name of the generated pfx password, used when <c>Proxy:RootCertificatePassword</c> is not set.</summary>
    public const string RootCertificatePasswordFileName = "root-ca.password";

    private const string SelfRequestMessage = "This port is the ai-proxy forward proxy, not an origin server. Set it as HTTP_PROXY/HTTPS_PROXY, not as a base URL.\n";

    private readonly ISystemProxyGuardLauncher _guardLauncher;
    private readonly ProviderRequestInspector _inspector;
    private readonly ILogger<TitaniumProxyService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _markerWait;
    private readonly ProxyRuntimeOptions _options;
    private readonly IProviderHosts _providerHosts;
    private readonly Func<CertificateManager, IRootCertificate> _rootCertificate;

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
        Func<HttpWebClient, bool>? upstreamClosed = null,
        Func<CertificateManager, IRootCertificate>? rootCertificate = null)
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
        _rootCertificate = rootCertificate ?? (static certificates => new TitaniumRootCertificate(certificates));
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
        Server.Logging.LoggerFactory = new TitaniumLoggerFactory(_loggerFactory);
        Server.ApplyLoggingConfiguration();
        Server.EnableHttpInterception = true;
        Server.MaxBufferedBodyBytes = 8 * 1024 * 1024;
        Server.ViaHeaderPseudonym = "";
        Server.BeforeRequest += BeforeRequestAsync;
        Server.BeforeResponse += BeforeResponseAsync;
        Server.AfterResponse += AfterResponseAsync;
        Server.EnableRequestTimingCapture = _inspector.CapturesResponses;

        var root = PrepareRootCertificate();
        if (_options.ConfigureSystemProxy)
        {
            _marker = await SystemProxyMarker.Acquire( _options.DataDirectory, _markerWait, _timeProvider);
        }

        _endPoint = new ExplicitProxyEndPoint(_options.ListenAddress, _options.Port);
        _endPoint.BeforeTunnelConnectRequest += BeforeTunnelConnectRequestAsync;
        Server.AddEndPoint(_endPoint);
        if (_options.TrustRootCertificate)
        {
            var trust = _rootCertificate(Server.CertificateManager);
            trust.EnsureTrusted();
            if (root.ReplacedThumbprint is { } old)
            {
                Untrust(trust, old);
            }
        }

        if (root.ReplacedThumbprint is { } replaced)
        {
            Log.RootReplaced(_logger, replaced);
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
        if (marker.DiscardedTornRecord)
        {
            Log.TornMarkerDiscarded(_logger);
        }

        if (marker.PreviousRunDied)
        {
            await RecoverPreviousRunAsync(marker);
        }

        var previousSettingsSnapshot = _systemProxyRegistrar.Capture();
        marker.Write(new IPEndPoint(_options.ListenAddress, _endPoint.Port), previousSettingsSnapshot);
        string appliedSettingsSnapshot;
        try
        {
            appliedSettingsSnapshot = _systemProxyRegistrar.Set(_endPoint);
        }
        catch
        {
            marker.Dispose();
            _marker = null;
            throw;
        }

        _systemProxySet = true;
        try
        {
            marker.RecordApplied(appliedSettingsSnapshot);
            _guard = _guardLauncher.Launch(_options.DataDirectory, () => Log.GuardExited(_logger));
        }
        catch
        {
            ReleaseSystemProxy();
            throw;
        }
    }

    /// <summary>Puts back what a crashed run left, but only while its settings are still current.</summary>
    private async Task RecoverPreviousRunAsync(SystemProxyMarker marker)
    {
        Log.PreviousRunDied(_logger, marker.PreviousEndpoint);
        if (marker.PreviousSettingsSnapshot is not null)
        {
            if (await new SystemProxyRecovery(_systemProxyRegistrar).RestoreIfOwned(marker))
            {
                Log.PreviousSettingsRestored(_logger, marker.PreviousEndpoint);
            }
            else
            {
                Log.PreviousProxyNoLongerOwned(_logger, marker.PreviousEndpoint);
            }

            return;
        }

        if (marker.PreviousEndpoint is { } endpoint && _systemProxyRegistrar.IsOwned(endpoint))
        {
            await _systemProxyRegistrar.ClearAsync();
            Log.PreviousProxyCleared(_logger, endpoint);
        }
        else
        {
            Log.PreviousProxyNoLongerOwned(_logger, marker.PreviousEndpoint);
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
                if (!_systemProxyRegistrar.Restore())
                {
                    Log.RestoreSkipped(_logger);
                }
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

    /// <summary>Removes a replaced root; a failure leaves it trusted but never stops the start, because the new root already works.</summary>
    private void Untrust(IRootCertificate trust, string thumbprint)
    {
        try
        {
            trust.Untrust(thumbprint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or CryptographicException)
        {
            Log.UntrustFailed(_logger, thumbprint, exception.Message);
        }
    }

    /// <summary>Loads or creates the constrained root and hands it to Titanium before Titanium can create or save one of its own.</summary>
    /// <remarks>Titanium 7.0.26's <c>CertificateManager.CreateRootCertificate</c> returns at once when <c>RootCertificate</c> is set,
    /// and <c>EnsureRootCertificate</c> calls it only while no root is set, so its overwrite-on-load-failure path never runs.
    /// Titanium is never given the password: it has no reason to open the pfx. The path stays set so that no Titanium fallback
    /// writes to its shared per-user directory.</remarks>
    private RootCertificateLoad PrepareRootCertificate()
    {
        CreatePrivateDirectory(_options.DataDirectory);
        var load = new RootCertificateFiles(_options.DataDirectory, _options.RootCertificatePassword, _providerHosts.Hosts, _timeProvider).Load();
        var certificates = Server.CertificateManager;
        certificates.RootCertificate = load.Root;
        certificates.PfxFilePath = Path.GetFullPath(Path.Combine(_options.DataDirectory, RootCertificateFileName));
        certificates.SaveFakeCertificates = false;
        certificates.DisableWildCardCertificates = true;
        return load;
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
        if (IsAddressedToSelf(e.HttpClient.Request.RequestUri))
        {
            var request = e.HttpClient.Request;
            Log.AddressedToSelf(_logger, request.Method, request.RequestUri.AbsolutePath, request.Headers.GetFirstHeader(HeaderNames.UserAgent)?.Value);
            e.GenericResponse(SelfRequestMessage, HttpStatusCode.BadRequest, [new HttpHeader(HeaderNames.ContentType, MediaTypeNames.Text.Plain)], true);
            return Task.CompletedTask;
        }

        return _inspector.BeforeRequestAsync(sender, e);
    }

    /// <summary>True when the request names this proxy's own listener as its origin, which Titanium cannot forward.</summary>
    private bool IsAddressedToSelf(Uri target)
    {
        if (_endPoint is null || target.Port != _endPoint.Port)
        {
            return false;
        }

        return target.IsLoopback || (IPAddress.TryParse(target.Host, out var address) && address.Equals(_options.ListenAddress));
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

    /// <summary>Decrypts a provider host; a harness-only host only when the CONNECT carries an ai-proxy credential.</summary>
    private Task BeforeTunnelConnectRequestAsync(object sender, TunnelConnectSessionEventArgs e)
    {
        var connect = e.HttpClient.ConnectRequest;
        var provider = !string.IsNullOrWhiteSpace(connect?.Host) && Uri.TryCreate($"https://{connect.Host}", UriKind.Absolute, out var address)
            ? _providerHosts.Find(address.Host)
            : null;
        e.DecryptSsl = provider is not null &&
                       (!provider.HarnessOnly ||
                        ProjectCredential.Read(connect!.Headers.GetFirstHeader(HeaderNames.ProxyAuthorization)?.Value) is not null);
        if (e.DecryptSsl && provider!.HarnessOnly)
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
        }

        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "AI proxy listening at {ProxyAddress}")]
        public static partial void Started(ILogger logger, Uri proxyAddress);

        [LoggerMessage(EventId = 1302, Level = LogLevel.Warning, Message = "A previous ai-proxy run ended with the system proxy set to {Endpoint}; checking whether it still owns the system proxy before setting it again")]
        public static partial void PreviousRunDied(ILogger logger, string? endpoint);

        [LoggerMessage(EventId = 1312, Level = LogLevel.Information, Message = "Restored the system proxy settings saved before the previous ai-proxy run at {Endpoint}")]
        public static partial void PreviousSettingsRestored(ILogger logger, string? endpoint);

        [LoggerMessage(EventId = 1313, Level = LogLevel.Warning, Message = "The system proxy no longer matches what the previous ai-proxy run at {Endpoint} set; left the current settings untouched")]
        public static partial void PreviousProxyNoLongerOwned(ILogger logger, string? endpoint);

        [LoggerMessage(EventId = 1314, Level = LogLevel.Warning, Message = "Cleared the system proxy the previous ai-proxy run left at {Endpoint}; that run kept no snapshot, so a proxy configured before it is not restored")]
        public static partial void PreviousProxyCleared(ILogger logger, string endpoint);

        [LoggerMessage(EventId = 1315, Level = LogLevel.Warning, Message = "The system proxy changed while ai-proxy ran; left the current settings untouched instead of restoring the earlier ones")]
        public static partial void RestoreSkipped(ILogger logger);

        [LoggerMessage(EventId = 1316, Level = LogLevel.Warning, Message = "The system proxy marker held a record cut short by an interrupted write; discarded it, since that run never set the system proxy")]
        public static partial void TornMarkerDiscarded(ILogger logger);

        [LoggerMessage(EventId = 1303, Level = LogLevel.Warning, Message = "The system proxy guard could not be reached; it has probably ended")]
        public static partial void GuardUnreachable(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1304, Level = LogLevel.Warning, Message = "The system proxy guard ended while ai-proxy was still running; a forced kill would now leave the system proxy set until the next start")]
        public static partial void GuardExited(ILogger logger);

        [LoggerMessage(EventId = 1305, Level = LogLevel.Error, Message = "The system proxy could not be restored; the marker stays, so the guard or the next start can retry the restore")]
        public static partial void RestoreFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1306, Level = LogLevel.Warning, Message = "The upstream {ProviderHost} closed its connection before the end of a chunked response; closing the client connection so the client sees the failure")]
        public static partial void UpstreamClosedMidStream(ILogger logger, string providerHost);

        [LoggerMessage(EventId = 1307, Level = LogLevel.Error, Message = "Titanium's upstream connection state could not be read; a client whose upstream drops a chunked response now waits until its own timeout")]
        public static partial void UpstreamStateUnreadable(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1308, Level = LogLevel.Warning, Message = "Replaced the ai-proxy root certificate {OldThumbprint} with a name-constrained one; restart every ai-proxy harness session so it reads the new root-ca.pem")]
        public static partial void RootReplaced(ILogger logger, string oldThumbprint);

        [LoggerMessage(EventId = 1311, Level = LogLevel.Warning, Message = "The replaced root certificate {Thumbprint} is still trusted and can be removed by hand: {Failure}")]
        public static partial void UntrustFailed(ILogger logger, string thumbprint, string failure);

        [LoggerMessage(EventId = 1309, Level = LogLevel.Warning, Message = "Answered 400 to {Method} {Path} (User-Agent: {UserAgent}) addressed to the proxy port itself; the client uses the proxy as a base URL instead of as an HTTP proxy")]
        public static partial void AddressedToSelf(ILogger logger, string method, string path, string? userAgent);
    }
}
