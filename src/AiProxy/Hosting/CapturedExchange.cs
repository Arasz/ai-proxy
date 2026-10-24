using AiProxy.Core.Capture;
using AiProxy.Core.Timing;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace AiProxy.Hosting;

/// <summary>What the inspector keeps about one exchange between BeforeRequest and AfterResponse.</summary>
internal sealed class CapturedExchange
{
    /// <summary>The handle of the original content when compression stored the request.</summary>
    public string? ContentId { get; set; }

    /// <summary>The capture id the request row was kept under; null when the request was not captured.</summary>
    public string? CaptureId { get; set; }

    /// <summary>The request body length as the client sent it.</summary>
    public long RequestBytes { get; set; }

    /// <summary>The request body length as the proxy forwarded it.</summary>
    public long ForwardedBytes { get; set; }

    /// <summary>The inspector's own stage timings.</summary>
    public StageTimings Stages { get; } = new();

    /// <summary>What is kept of the response body; chosen from the response headers, kept whole until they are seen.</summary>
    public ResponseBuffer? Response { get; set; }

    /// <summary>Copies response bytes as they pass on to the client; never holds them back.</summary>
    public void OnDataReceived(object? sender, DataEventArgs data)
    {
        Response ??= ResponseBuffer.For(null, null);
        Response.Append(data.Buffer.AsSpan(data.Offset, data.Count));
    }
}
