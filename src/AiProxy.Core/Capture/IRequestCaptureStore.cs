namespace AiProxy.Core.Capture;

/// <summary>Keeps every provider request the proxy reads, when capture is on.</summary>
public interface IRequestCaptureStore
{
    /// <summary>Whether this store keeps anything; the proxy reads and times exchanges only when it does.</summary>
    bool Keeps => true;

    /// <summary>Keeps one request; a failure is the caller's to log, never the request's to carry.</summary>
    ValueTask StoreAsync(CapturedRequest request, CancellationToken cancellationToken);

    /// <summary>Keeps the numbers of the response to a captured request; a failure is the caller's to log.</summary>
    ValueTask StoreResponseAsync(CapturedResponse response, CancellationToken cancellationToken);
}