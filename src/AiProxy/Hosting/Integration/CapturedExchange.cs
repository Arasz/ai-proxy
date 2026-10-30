using AiProxy.Core.Capture;
using AiProxy.Core.Gain;
using AiProxy.Core.Providers;
using AiProxy.Core.Timing;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace AiProxy.Hosting.Integration;

/// <summary>What the inspector keeps about one exchange between BeforeRequest and AfterResponse.</summary>
public sealed class CapturedExchange
{
    public string GainId { get; } = Guid.CreateVersion7().ToString();
    public string? GainProject { get; init; }
    public string GainRule { get; set; } = "unknown";
    public string GainMethod { get; set; } = "POST";
    public string GainLayer { get; set; } = "unknown";
    public string GainPipeline { get; set; } = "unknown";
    public List<GainFailure> GainFailures { get; } = [];

    /// <summary>Whether the request is a model call; anything else is recorded as other traffic, outside the savings totals.</summary>
    public bool IsModelCall { get; init; } = true;

    /// <summary>The provider dialect identified from the request host.</summary>
    public required Dialect Dialect { get; init; }

    /// <summary>The handle of the original content when compression stored the request.</summary>
    public string? ContentId { get; set; }

    /// <summary>The capture id the request row was kept under; null when the request was not captured.</summary>
    public string? CaptureId { get; set; }

    /// <summary>The request body length as the client sent it.</summary>
    public long RequestBytes { get; set; }

    /// <summary>The request body length as the proxy forwarded it.</summary>
    public long ForwardedBytes { get; set; }

    /// <summary>Whether the proxy answers the client itself through the CCR relay, so the response is not Titanium's to read.</summary>
    public bool Relayed { get; set; }

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