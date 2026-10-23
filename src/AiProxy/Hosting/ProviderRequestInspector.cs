using AiProxy.Core.Compression;
using Titanium.Web.Proxy.EventArguments;

namespace AiProxy.Hosting;

/// <summary>Inspects and compresses requests sent to provider API hosts.</summary>
public sealed class ProviderRequestInspector
{
    private const long MaxInspectedBodyBytes = 8 * 1024 * 1024;

    private readonly ICompressionPipeline _compression;
    private readonly IProviderHosts _providerHosts;
    private readonly bool _compressionEnabled;

    /// <summary>Creates an inspector; with compression disabled every request passes through untouched.</summary>
    public ProviderRequestInspector(ICompressionPipeline compression, IProviderHosts providerHosts, bool compressionEnabled)
    {
        ArgumentNullException.ThrowIfNull(compression);
        ArgumentNullException.ThrowIfNull(providerHosts);
        _compression = compression;
        _providerHosts = providerHosts;
        _compressionEnabled = compressionEnabled;
    }

    /// <summary>Leaves unknown hosts untouched and processes known provider requests.</summary>
    public async Task BeforeRequestAsync(object sender, SessionEventArgs e)
    {
        if (!_compressionEnabled)
        {
            return;
        }

        var request = e.HttpClient.Request;
        if (_providerHosts.Find(request.RequestUri.Host) is not { } dialect || !request.HasBody)
        {
            return;
        }

        if (request.ContentLength > MaxInspectedBodyBytes)
        {
            return;
        }

        var body = await e.GetRequestBody(CancellationToken.None);
        var compressed = await _compression.CompressAsync(
            new CompressionRequest(body, request.ContentType, dialect),
            CancellationToken.None);
        if (compressed.Changed)
        {
            e.SetRequestBody(compressed.Body.ToArray());
            if (compressed.Summary is { } summary)
            {
                e.UserData = summary.RequestId;
            }
        }
    }

    /// <summary>Returns the original-content handle when compression stored a request.</summary>
    public Task BeforeResponseAsync(object sender, SessionEventArgs e)
    {
        if (e.UserData is string requestId)
        {
            e.HttpClient.Response.Headers.AddHeader("x-ai-proxy-content-id", requestId);
        }

        return Task.CompletedTask;
    }
}
