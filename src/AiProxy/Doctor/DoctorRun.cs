using System.Globalization;
using System.Net.Sockets;
using AiProxy.Cli;
using AiProxy.Core.Doctor;
using AiProxy.Hosting.Proxy;

namespace AiProxy.Doctor;

/// <summary>Runs every setup check against one proxy and gathers the report.</summary>
internal sealed class DoctorRun(DoctorSettings settings, TimeProvider time, Func<string, string?> variable)
{
    private const string ProxyPortVariable = "Proxy__Port";
    private const string LegacyProxyPortVariable = "PROXY__PORT";

    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(settings.Timeout);

    public async Task<DoctorReport> RunAsync(CancellationToken cancellationToken)
    {
        var proxy = ProxyAddress();
        var dataDirectory = Path.GetFullPath(settings.DataDirectory ?? ProxyRuntimeOptions.DefaultDataDirectory);
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = _timeout };
        var api = await new ApiCheck(http, new Uri(settings.Endpoint), EntryPoint.ProductVersion).ProbeAsync(cancellationToken).ConfigureAwait(false);

        CheckResult first = api.Result;
        CheckResult inspection;
        if (!api.Reachable && !await IsListeningAsync(proxy, _timeout, cancellationToken).ConfigureAwait(false))
        {
            first = new CheckResult(ApiCheck.Name, CheckStatus.Fail, $"The proxy is not running: nothing answers at {EndpointText.Safe(settings.Endpoint)} or listens at {EndpointText.Safe(proxy.ToString())}.", "Start ai-proxy.");
            inspection = new CheckResult(InspectionCheck.Name, CheckStatus.Unknown, "skipped because the proxy is not running.");
        }
        else
        {
            inspection = await new InspectionCheck(proxy, settings.Host, dataDirectory, _timeout).RunAsync(cancellationToken).ConfigureAwait(false);
        }

        var root = await new RootCheck(dataDirectory, time, new SystemRootTrust()).RunAsync(cancellationToken).ConfigureAwait(false);
        var environment = await new EnvironmentCheck(proxy, variable).RunAsync(cancellationToken).ConfigureAwait(false);
        return new DoctorReport([first, inspection, root, environment]);
    }

    private Uri ProxyAddress()
    {
        if (settings.Proxy is not null)
        {
            return new Uri(settings.Proxy);
        }

        var port = int.TryParse(variable(ProxyPortVariable) ?? variable(LegacyProxyPortVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var configured)
            ? configured
            : ProxyRuntimeOptions.Default.Port;
        return new Uri(string.Format(CultureInfo.InvariantCulture, KnownNames.Network.ProxyUrlFormat, port));
    }

    private static async Task<bool> IsListeningAsync(Uri proxy, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var connection = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await connection.ConnectAsync(proxy.Host, proxy.Port, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
