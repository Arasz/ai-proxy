namespace AiProxy.Core.Compression;

/// <summary>Works on a request body before the request is forwarded to its provider.</summary>
public interface ICompressionPipeline
{
    /// <summary>Whether this pipeline can change a body; the proxy reads provider bodies for it only when it can.</summary>
    bool Compresses => true;

    /// <summary>Compresses the body, or leaves it alone.</summary>
    ValueTask<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken);
}
