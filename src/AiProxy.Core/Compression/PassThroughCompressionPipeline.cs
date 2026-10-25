namespace AiProxy.Core.Compression;

/// <summary>The pipeline that changes nothing, which is the state of the question today.</summary>
/// <remarks>
/// The compression question is the open one about what this project is for, and it is
/// answered in a later phase. What this phase builds is the seam, and the seam's
/// guarantee is that the body a provider request carries is byte for byte the body the
/// caller sent, until a pipeline that changes it replaces this one.
/// </remarks>
public sealed class PassThroughCompressionPipeline : ICompressionPipeline
{
    /// <inheritdoc />
    public bool Compresses => false;

    /// <summary>Returns the body unchanged.</summary>
    public ValueTask<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ValueTask.FromResult(new CompressionResult(request.Body, Changed: false));
    }
}
