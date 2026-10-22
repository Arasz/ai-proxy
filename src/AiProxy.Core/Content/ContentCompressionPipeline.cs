using System.Text;
using System.Text.Json;
using AiProxy.Core.Compression;

namespace AiProxy.Core.Content;

/// <summary>Shrinks the tool output in a request body, and keeps what it dropped.</summary>
public sealed class ContentCompressionPipeline : ICompressionPipeline
{
    private readonly ToolOutputRegionFinder _finder;
    private readonly IDecompositor _decomposer;
    private readonly ICompressionExecutor _executor;
    private readonly ICompositor _compositor;
    private readonly IContentStore _store;
    private readonly CompressionOptions _options;

    /// <summary>Holds the collaborators the pipeline is composed of.</summary>
    /// <param name="finder">Finds the tool output in the body.</param>
    /// <param name="decomposer">Splits a region into the parts it carries.</param>
    /// <param name="executor">Runs the reducer of each part.</param>
    /// <param name="compositor">Puts the reduced parts back.</param>
    /// <param name="store">Keeps what the pipeline changed.</param>
    /// <param name="options">The operator's settings.</param>
    public ContentCompressionPipeline(
        ToolOutputRegionFinder finder,
        IDecompositor decomposer,
        ICompressionExecutor executor,
        ICompositor compositor,
        IContentStore store,
        CompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(finder);
        ArgumentNullException.ThrowIfNull(decomposer);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _finder = finder;
        _decomposer = decomposer;
        _executor = executor;
        _compositor = compositor;
        _store = store;
        _options = options;
    }

    /// <inheritdoc />
    public async ValueTask<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Body.Length == 0)
        {
            return new CompressionResult(request.Body, false);
        }

        var regions = _finder.Find(request.Body);
        if (regions.Count == 0)
        {
            return new CompressionResult(request.Body, false);
        }

        var requestId = Guid.CreateVersion7().ToString();
        var original = Encoding.UTF8.GetString(request.Body.Span);
        var body = request.Body.ToArray();
        var parts = new List<CompressedPart>();
        var replacements = new List<(int Start, int Length, byte[] Bytes)>();

        foreach (var region in regions.OrderBy(region => region.Start))
        {
            var reduced = await ReduceAsync(region.Text, requestId, parts, cancellationToken).ConfigureAwait(false);
            if (reduced is null)
            {
                continue;
            }

            replacements.Add((region.Start, region.Length, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reduced))));
        }

        if (replacements.Count == 0)
        {
            return new CompressionResult(request.Body, false);
        }

        var compressed = Rebuild(body, replacements);
        var compressedText = Encoding.UTF8.GetString(compressed);
        var record = new ContentRecord(
            requestId,
            DateTimeOffset.UtcNow,
            original,
            compressedText,
            original.Length == 0 ? 1.0 : (double)compressedText.Length / original.Length,
            string.Join(",", parts.Select(part => part.Path).Distinct(StringComparer.Ordinal)),
            parts.Count,
            parts);

        string? failure = null;
        try
        {
            await _store.StoreAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception.Message;
        }

        return new CompressionResult(compressed, true, failure);
    }

    private static byte[] Rebuild(byte[] body, List<(int Start, int Length, byte[] Bytes)> replacements)
    {
        using var buffer = new MemoryStream(body.Length);
        var position = 0;

        foreach (var replacement in replacements)
        {
            buffer.Write(body, position, replacement.Start - position);
            buffer.Write(replacement.Bytes);
            position = replacement.Start + replacement.Length;
        }

        buffer.Write(body, position, body.Length - position);

        return buffer.ToArray();
    }

    private async ValueTask<string?> ReduceAsync(
        string region,
        string requestId,
        List<CompressedPart> parts,
        CancellationToken cancellationToken)
    {
        var decomposition = _decomposer.Decompose(region, requestId);
        if (decomposition.Parts.Count == 0)
        {
            return null;
        }

        var reduced = new List<CompressedPart>(decomposition.Parts.Count);
        var changed = false;

        foreach (var part in decomposition.Parts)
        {
            if (part.Text.Length < _options.MinimumFor(part.Type))
            {
                reduced.Add(new CompressedPart(part.ChunkId, part.Type, part.Text, part.Text, "below-minimum", 1.0));
                continue;
            }

            var compressed = await _executor.ExecuteAsync(part, cancellationToken).ConfigureAwait(false);
            reduced.Add(compressed);
            changed |= compressed.Text.Length < part.Text.Length;
        }

        parts.AddRange(reduced);
        if (!changed)
        {
            return null;
        }

        return _compositor.Compose(decomposition, reduced);
    }
}
