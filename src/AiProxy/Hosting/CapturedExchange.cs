using AiProxy.Core.Capture;
using AiProxy.Core.Timing;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace AiProxy.Hosting;

/// <summary>What the inspector keeps about one exchange between BeforeRequest and AfterResponse.</summary>
internal sealed class CapturedExchange
{
    /// <summary>The most response bytes kept for reading usage; a longer body is counted, not kept.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    private MemoryStream? _response = new();

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

    /// <summary>Every response body byte that passed, kept or not.</summary>
    public long ResponseBytes { get; private set; }

    /// <summary>Whether the body outgrew <see cref="MaxResponseBytes"/>, so it was not kept.</summary>
    public bool OverCap => _response is null;

    /// <summary>The kept response body; empty when <see cref="OverCap"/>.</summary>
    public ReadOnlySpan<byte> Response => _response is null ? [] : _response.GetBuffer().AsSpan(0, (int)_response.Length);

    /// <summary>Copies response bytes as they pass on to the client; never holds them back.</summary>
    public void OnDataReceived(object? sender, DataEventArgs data)
    {
        ResponseBytes += data.Count;
        if (_response is null)
        {
            return;
        }

        if (_response.Length + data.Count > MaxResponseBytes)
        {
            _response.Dispose();
            _response = null;
            return;
        }

        _response.Write(data.Buffer, data.Offset, data.Count);
    }
}
