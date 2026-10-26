namespace AiProxy.Core.Capture;

/// <summary>The token counts a provider reported for one response; a count the provider left out is null.</summary>
public sealed record ResponseUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CacheCreationInputTokens,
    long? CacheReadInputTokens);

/// <summary>What reading a response body gave: the usage, or why there is none.</summary>
/// <param name="Usage">The counts read; null when <paramref name="Error" /> says why not.</param>
/// <param name="Error">A short reason such as <c>malformed</c> or <c>encoding:zstd</c>; null when usage was read.</param>
public sealed record UsageReading(ResponseUsage? Usage, string? Error);