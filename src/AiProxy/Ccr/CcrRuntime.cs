using AiProxy.Configuration;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using AiProxy.Hosting.Integration;
using Titanium.Web.Proxy.EventArguments;

namespace AiProxy.Ccr;

/// <summary>Plans recoverable compression for a provider request and relays the requests it takes over.</summary>
public interface ICcrRuntime
{
    /// <summary>Decides what the proxy does with the request.</summary>
    Task<CcrPlan> DecideAsync(Dialect dialect, string method, string path, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    /// <summary>Sends a relayed request upstream and answers its client.</summary>
    Task RelayAsync(SessionEventArgs e, CapturedExchange exchange, byte[] body, CcrPlan plan, IReadOnlyDictionary<string, string> originals);
}

/// <summary>Recoverable compression with the operator's settings, the store and the relay.</summary>
/// <param name="settings">The operator's CCR settings.</param>
/// <param name="store">The originals and the thread ledger.</param>
/// <param name="relay">Answers the client of a relayed request.</param>
public sealed class CcrRuntime(CcrSettings settings, IOriginalStore store, CcrRelay relay) : ICcrRuntime
{
    /// <inheritdoc />
    public Task<CcrPlan> DecideAsync(Dialect dialect, string method, string path, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        return CcrPlan.DecideAsync(settings, store, dialect, method, path, body, cancellationToken);
    }

    /// <inheritdoc />
    public Task RelayAsync(SessionEventArgs e, CapturedExchange exchange, byte[] body, CcrPlan plan, IReadOnlyDictionary<string, string> originals)
    {
        return relay.RelayAsync(e, exchange, body, plan, originals);
    }
}

/// <summary>Recoverable compression switched off with compression: every request is forwarded as before.</summary>
public sealed class CcrOff : ICcrRuntime
{
    /// <inheritdoc />
    public Task<CcrPlan> DecideAsync(Dialect dialect, string method, string path, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        return Task.FromResult(CcrPlan.Forward);
    }

    /// <inheritdoc />
    public Task RelayAsync(SessionEventArgs e, CapturedExchange exchange, byte[] body, CcrPlan plan, IReadOnlyDictionary<string, string> originals)
    {
        throw new InvalidOperationException("CCR is off; no request is relayed.");
    }
}