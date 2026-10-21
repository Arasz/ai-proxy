using System.Text;
using AiProxy.Core.Compression;

namespace AiProxy.Tests.Integration;

/// <summary>A pipeline that leaves a mark on the body, so a test can see that it ran.</summary>
internal sealed class MarkerCompressionPipeline : ICompressionPipeline
{
    /// <summary>What the pipeline adds to the body.</summary>
    public const string Marker = "\n//compressed";

    /// <summary>Marks the body.</summary>
    public ValueTask<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(request.Body.Span) + Marker);
        return ValueTask.FromResult(new CompressionResult(body, Changed: true));
    }
}
