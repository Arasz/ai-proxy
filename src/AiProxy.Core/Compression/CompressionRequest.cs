using AiProxy.Core.Providers;

namespace AiProxy.Core.Compression;

/// <summary>The request body a compression pipeline is asked to work on.</summary>
/// <param name="Body">The request body.</param>
/// <param name="ContentType">The content type of the body, when it has one.</param>
/// <param name="Dialect">The provider dialect the request speaks.</param>
public sealed record CompressionRequest(ReadOnlyMemory<byte> Body, string? ContentType, Dialect Dialect);
