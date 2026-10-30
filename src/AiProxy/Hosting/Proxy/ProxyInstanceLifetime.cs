using System.Diagnostics;
using System.Globalization;
using AiProxy.Configuration;
using AiProxy.Hosting.Integration;
using AiProxy.Hosting.Marker;

namespace AiProxy.Hosting.Proxy;

internal sealed record ProxyInstanceLifetime(string DataDirectory, WebApplicationBuilder Builder, ProxyProbe ProxyProbe, ProxySettings Settings, IMarkerOwnerSearch MarkerOwnerSearch) : IAsyncDisposable
{

    private const string ProxyName = "ai-proxy";
    private RuntimeLease _runtimeLease = RuntimeLease.NoOpLease;

    private StartupLease _startupLease = StartupLease.NoOpLease;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _startupLease.DisposeAsync();
        await _runtimeLease.DisposeAsync();
    }

    public async Task<int> AcquireAndStartProxyInstance(string[] arguments, CancellationToken cancellationToken = default)
    {
        _startupLease = AcquireStartupLeaseOrFail();

        try
        {
            _runtimeLease = ProxyLease.AcquireRuntimeLease(DataDirectory);
        }
        catch (IOException exception) when (!arguments.Contains(ProxyCommand.RestartFlag, StringComparer.Ordinal))
        {
            var owner = await ProxyControl.TryReadOwnerAsync(DataDirectory, cancellationToken);
            var running = owner is null
                ? "ai-proxy is already running"
                : $"ai-proxy is already running (pid {owner.Pid}, port {new Uri(owner.ProxyUrl).Port})";
            throw new IOException($"{running}. Use `ai-proxy {ProxyCommand.RestartFlag}` to replace it.", exception);
        }
        catch (IOException) when (arguments.Contains(ProxyCommand.RestartFlag, StringComparer.Ordinal))
        {
            ProxyControl.ProxyInfo previous;
            try
            {
                previous = await ProxyControl.StopOwnedAsync(DataDirectory, cancellationToken, liveProxy => ProxyProbe.ProbeNewProxyPorts(liveProxy));
            }
            catch (OperationCanceledException exception)
            {
                throw new IOException("The ai-proxy control peer did not finish shutdown within the deadline. Stop the owner manually, then retry.", exception);
            }

            if (Settings.Port is null)
            {
                Builder.Configuration[KnownNames.Configuration.ProxyPort] = new Uri(previous.ProxyUrl).Port.ToString(CultureInfo.InvariantCulture);
            }

            if (!Builder.Configuration.HasApiConfiguration())
            {
                Builder.WebHost.UseUrls(previous.ApiUrls);
            }

            _runtimeLease = ProxyLease.AcquireRuntimeLease(DataDirectory);
        }

        await ProxyProbe.ProbeSystemProxyAddresses();

        return await StartProxyApplication(_startupLease);
    }


    private async Task<int> StartProxyApplication(StartupLease startupLease)
    {
        await using var app = ProxyApplication.Create(Builder);
        await app.StartAsync();
        using var process = Process.GetCurrentProcess();
        var proxyInfo = new ProxyControl.ProxyInfo(ProxyName,
            ProxyControl.DefaultProtocol,
            Guid.CreateVersion7().ToString("N"),
            process.Id,
            process.StartTime.ToUniversalTime(),
            app.Services.GetRequiredService<TitaniumProxyService>().Address.AbsoluteUri.TrimEnd('/'),
            [.. app.Urls],
            new ProcessIdentity(process).StartToken);

        await using var control = new ProxyControl(DataDirectory, proxyInfo, app.Lifetime);
        await control.Start(startupLease);
        await app.WaitForShutdownAsync();
        return 0;
    }

    private StartupLease AcquireStartupLeaseOrFail()
    {
        try
        {
            return ProxyLease.AcquireStartupLease(DataDirectory);
        }
        catch (IOException exception)
        {
            throw new IOException("Another ai-proxy start or restart is in progress. Wait for it to finish, then retry.", exception);
        }
    }
}
