using AiProxy.Core.Timing;

namespace AiProxy.Core.Capture;

/// <summary>The numbers of one captured exchange's response: sizes, token usage and stage timings, never content.</summary>
/// <param name="RequestId">The id of the <see cref="CapturedRequest"/> this response answered.</param>
/// <param name="CompletedAt">When the response finished passing through the proxy.</param>
/// <param name="StatusCode">The upstream's HTTP status code.</param>
/// <param name="RequestBytes">The request body length as the client sent it.</param>
/// <param name="ForwardedBytes">The request body length as the proxy forwarded it.</param>
/// <param name="ResponseBytes">The response body length on the wire, still content-encoded.</param>
/// <param name="Usage">The token usage read from the response; null when <paramref name="UsageError"/> says why not.</param>
/// <param name="UsageError">Why usage was not read, such as <c>over-cap</c> or <c>malformed</c>; null when it was.</param>
/// <param name="Stages">How long each step of handling the exchange took.</param>
public sealed record CapturedResponse(
    string RequestId,
    DateTimeOffset CompletedAt,
    int StatusCode,
    long RequestBytes,
    long ForwardedBytes,
    long ResponseBytes,
    ResponseUsage? Usage,
    string? UsageError,
    IReadOnlyList<StageTiming> Stages);
