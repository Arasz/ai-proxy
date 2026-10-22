namespace AiProxy.Core.Compression;

/// <summary>Works on a request body before the request is forwarded to its provider.</summary>
public interface ICompressionPipeline
{
    /// <summary>Compresses the body, or leaves it alone.</summary>
    ValueTask<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken);
}
