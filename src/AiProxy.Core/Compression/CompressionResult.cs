namespace AiProxy.Core.Compression;

/// <summary>The body a compression pipeline produced.</summary>
/// <param name="Body">The body to forward.</param>
/// <param name="Changed">Whether the pipeline changed the body at all.</param>
/// <param name="Failure">What went wrong on the way, when something did and the body was still produced.</param>
public sealed record CompressionResult(ReadOnlyMemory<byte> Body, bool Changed, string? Failure = null);
