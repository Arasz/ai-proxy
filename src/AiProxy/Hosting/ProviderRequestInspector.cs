using AiProxy.Core.Compression;
using AiProxy.Core.Providers;
using Titanium.Web.Proxy.EventArguments;

namespace AiProxy.Hosting;

/// <summary>Inspects and compresses requests sent to the canonical provider API hosts.</summary>
public sealed class ProviderRequestInspector
{
    private readonly ICompressionPipeline _compression;

    /// <summary>Creates an inspector using the configured compression pipeline.</summary>
    public ProviderRequestInspector(ICompressionPipeline compression)
    {
        ArgumentNullException.ThrowIfNull(compression);
        _compression = compression;
    }

    /// <summary>Leaves unknown hosts untouched and processes known provider requests.</summary>
    public async Task BeforeRequestAsync(object sender, SessionEventArgs e)
    {
        var request = e.HttpClient.Request;
        if (ProviderHostCatalog.Find(request.RequestUri.Host) is not { } dialect || !request.HasBody)
        {
            return;
        }

        if (_compression is PassThroughCompressionPipeline)
        {
            return;
        }

        if (request.ContentLength > 8 * 1024 * 1024)
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
