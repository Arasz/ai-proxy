using System.Text;
using System.Text.Json;
using AiProxy.Core.Timing;
using AiProxy.Core.Compression;

namespace AiProxy.Core.Content;

/// <summary>Shrinks the tool output in a request body, and keeps what it dropped.</summary>
/// <param name="finder">Finds the tool output in the body.</param>
/// <param name="decomposer">Splits a region into the parts it carries.</param>
/// <param name="executor">Runs the reducer of each part.</param>
/// <param name="compositor">Puts the reduced parts back.</param>
/// <param name="store">Keeps what the pipeline changed.</param>
/// <param name="options">The operator's settings.</param>
/// <param name="time">Stamps the records.</param>
public sealed class ContentCompressionPipeline(
    ToolOutputRegionFinder finder,
    IDecompositor decomposer,
    ICompressionExecutor executor,
    ICompositor compositor,
    IContentStore store,
    CompressionOptions options,
    TimeProvider time) : ICompressionPipeline
{
    /// <summary>The path recorded for a part whose reducer threw, so the part kept its original text.</summary>
    public const string HandlerFailedPath = "compression.handler-failed";

    /// <inheritdoc />
    public async ValueTask<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken)
    {
        if (request.Body.Length == 0)
        {
            return new CompressionResult(request.Body, false);
        }

        var stages = new StageTimings();
        var started = time.GetTimestamp();
        var regions = finder.Find(request.Body);
        stages.Add("find", time.GetElapsedTime(started));
        if (regions.Count == 0)
        {
            return new CompressionResult(request.Body, false) { Stages = stages.ToArray() };
        }

        var requestId = Guid.CreateVersion7().ToString();
        var original = Encoding.UTF8.GetString(request.Body.Span);
        var body = request.Body.ToArray();
        var parts = new List<CompressedPart>();
        var replacements = new List<(int Start, int Length, byte[] Bytes)>();
        var failures = new List<PartFailure>();

        foreach (var region in WithoutOverlaps(regions))
        {
            var reduced = await ReduceAsync(region, requestId, parts, failures, stages, cancellationToken).ConfigureAwait(false);
            if (reduced is null)
            {
                continue;
            }

            replacements.Add((region.Start, region.Length, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reduced))));
        }

        if (replacements.Count == 0)
        {
            return new CompressionResult(request.Body, false) { PartFailures = failures, Stages = stages.ToArray() };
        }

        started = time.GetTimestamp();
        var compressed = Rebuild(body, replacements);
        stages.Add("rebuild", time.GetElapsedTime(started));
        var compressedText = Encoding.UTF8.GetString(compressed);
        var ratio = body.Length == 0 ? 1.0 : (double)compressed.Length / body.Length;
        var path = string.Join(",", parts.Select(part => part.Path).Distinct(StringComparer.Ordinal));
        var record = new ContentRecord(
            requestId,
            time.GetUtcNow(),
            original,
            compressedText,
            ratio,
            path,
            parts.Count,
            parts);

        string? failure = null;
        started = time.GetTimestamp();
        try
        {
            await store.StoreAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception.Message;
        }

        stages.Add("store", time.GetElapsedTime(started));

        var summary = new CompressionSummary(requestId, path, parts.Count, ratio);

        return new CompressionResult(compressed, true, failure, summary) { PartFailures = failures, Stages = stages.ToArray() };
    }

    /// <summary>Keeps the regions that do not overlap the one before them.</summary>
    /// <remarks>
    /// The finder returns the string tokens of a body, and the tokens of a body do not
    /// overlap, so the regions never do either. A region that started inside the one
    /// before it would make the rebuild write a negative number of bytes, so a region
    /// that does is left alone rather than trusted.
    /// </remarks>
    /// <param name="regions">The regions the finder found.</param>
    public static IReadOnlyList<ContentRegion> WithoutOverlaps(IReadOnlyList<ContentRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);

        var kept = new List<ContentRegion>(regions.Count);
        var end = 0;

        foreach (var region in regions.OrderBy(candidate => candidate.Start))
        {
            if (region.Start < end)
            {
                continue;
            }

            kept.Add(region);
            end = region.Start + region.Length;
        }

        return kept;
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
        ContentRegion region,
        string requestId,
        List<CompressedPart> parts,
        List<PartFailure> failures,
        StageTimings stages,
        CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var decomposition = decomposer.Decompose(region.Text, requestId, region.FilePath);
        stages.Add("decompose", time.GetElapsedTime(started));
        if (decomposition.Parts.Count == 0)
        {
            return null;
        }

        var reduced = new List<CompressedPart>(decomposition.Parts.Count);
        var changed = false;

        foreach (var part in decomposition.Parts)
        {
            if (part.Text.Length < options.MinimumFor(part.Type))
            {
                reduced.Add(new CompressedPart(part.ChunkId, part.Type, part.Text, part.Text, "below-minimum", 1.0));
                continue;
            }

            CompressedPart compressed;
            started = time.GetTimestamp();
            try
            {
                compressed = await executor.ExecuteAsync(part, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(new PartFailure(part.ChunkId, part.Type.ToString(), exception));
                compressed = new CompressedPart(part.ChunkId, part.Type, part.Text, part.Text, HandlerFailedPath, 1.0);
            }

            stages.Add($"reduce.{part.Type}", time.GetElapsedTime(started));
            reduced.Add(compressed);
            changed |= compressed.Text.Length < part.Text.Length;
        }

        parts.AddRange(reduced);
        if (!changed)
        {
            return null;
        }

        started = time.GetTimestamp();
        var composed = compositor.Compose(decomposition, reduced);
        stages.Add("compose", time.GetElapsedTime(started));
        return composed;
    }
}
