using AiProxy.Core.Providers;

namespace AiProxy.Core.Capture;

/// <summary>One provider request as the client sent it, kept to measure how requests are made up.</summary>
/// <param name="RequestId">The id the capture is kept under.</param>
/// <param name="CapturedAt">When the proxy read the request.</param>
/// <param name="Provider">The provider dialect the host belongs to.</param>
/// <param name="Host">The provider host the request was addressed to.</param>
/// <param name="Path">The request path, without the query string.</param>
/// <param name="Body">The body as Titanium read it, with zstd decoded too; a zstd body that does not decode is kept as sent, still encoded.</param>
public sealed record CapturedRequest(
    string RequestId,
    DateTimeOffset CapturedAt,
    Dialect Provider,
    string Host,
    string Path,
    ReadOnlyMemory<byte> Body);