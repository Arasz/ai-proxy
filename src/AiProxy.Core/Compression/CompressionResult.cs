namespace AiProxy.Core.Compression;

/// <summary>The body a compression pipeline produced.</summary>
/// <param name="Body">The body to forward.</param>
/// <param name="Changed">Whether the pipeline changed the body at all.</param>
public sealed record CompressionResult(ReadOnlyMemory<byte> Body, bool Changed);
