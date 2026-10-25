using System.Text;
using System.Text.Json;
using AiProxy.Core.Timing;
using AiProxy.Core.Compression;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Core.Content;

/// <summary>Shrinks the tool output in a request body, and keeps what it dropped.</summary>
/// <param name="finder">Finds the tool output in the body.</param>
/// <param name="decomposer">Splits a region into the parts it carries.</param>
/// <param name="executor">Runs the reducer of each part.</param>
/// <param name="compositor">Puts the reduced parts back.</param>
/// <param name="store">Keeps what the pipeline changed.</param>
/// <param name="options">The operator's settings.</param>
/// <param name="time">Stamps the records.</param>
/// <param name="previewer">Turns a large region into its preview when the request has CCR on.</param>
public sealed class ContentCompressionPipeline(
    ToolOutputRegionFinder finder,
    IDecompositor decomposer,
    ICompressionExecutor executor,
    ICompositor compositor,
    IContentStore store,
    CompressionOptions options,
    TimeProvider time,
    CcrPreviewer previewer) : ICompressionPipeline
{
    /// <summary>The path recorded for a part whose reducer threw, so the part kept its original text.</summary>
    public const string HandlerFailedPath = "compression.handler-failed";

    /// <summary>The path recorded when at least one region was sent as a CCR preview.</summary>
    public const string PreviewPath = "ccr.preview";

    /// <summary>The chunk id a failed CCR keep is reported under, since it belongs to a region rather than a part.</summary>
    public const int RegionChunkId = -1;

    /// <summary>The path a failure is reported under when a code view's original could not be kept, so the region went out unchanged.</summary>
    public const string OriginalNotKeptPath = "ccr.original-not-kept";

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
        var replacements = new List<Replacement>();
        var failures = new List<PartFailure>();
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var region in WithoutOverlaps(regions))
        {
            var before = parts.Count;
            var reduced = await ReduceAsync(region, requestId, request.Ccr, parts, failures, stages, cancellationToken);
            if (reduced is not null && !await KeepViewsAsync(parts.Skip(before).ToArray(), request.Ccr, originals, failures, cancellationToken))
            {
                parts.RemoveRange(before, parts.Count - before);
                continue;
            }

            var preview = await PreviewAsync(region, reduced ?? region.Text, request.Ccr, failures, stages, cancellationToken);
            if (preview is not null)
            {
                originals[preview.Id] = region.Text;
            }

            var sent = preview?.Text ?? reduced;
            if (sent is null)
            {
                continue;
            }

            replacements.Add(new Replacement(region.Start, region.Length, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sent))));
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
        var paths = parts.Select(static part => part.Path);
        var path = string.Join(",", (originals.Count > 0 ? paths.Append(PreviewPath) : paths).Distinct(StringComparer.Ordinal));
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

        return new CompressionResult(compressed, true, failure, summary) { PartFailures = failures, Stages = stages.ToArray(), Originals = originals };
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
        var kept = new List<ContentRegion>(regions.Count);
        var end = 0;

        foreach (var region in regions.OrderBy(static region => region.Start))
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

    private static byte[] Rebuild(byte[] body, List<Replacement> replacements)
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

    private async ValueTask<CcrPreview?> PreviewAsync(
        ContentRegion region,
        string reduced,
        CcrMode mode,
        List<PartFailure> failures,
        StageTimings stages,
        CancellationToken cancellationToken)
    {
        if (mode == CcrMode.Off)
        {
            return null;
        }

        var started = time.GetTimestamp();
        try
        {
            return await previewer.PreviewAsync(region.Text, reduced, mode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            failures.Add(new PartFailure(RegionChunkId, PreviewPath, exception));
            return null;
        }
        finally
        {
            stages.Add("ccr.preview", time.GetElapsedTime(started));
        }
    }

    /// <summary>Keeps the original of every code view under the id its hint names; false when one could not be kept.</summary>
    private async ValueTask<bool> KeepViewsAsync(
        IReadOnlyList<CompressedPart> reduced,
        CcrMode mode,
        Dictionary<string, string> originals,
        List<PartFailure> failures,
        CancellationToken cancellationToken)
    {
        foreach (var part in reduced.Where(static part => part.Path == SourceCodeCompressionHandler.CodeViewPath))
        {
            try
            {
                originals[await previewer.KeepAsync(part.OriginalText, mode, cancellationToken).ConfigureAwait(false)] = part.OriginalText;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(new PartFailure(part.ChunkId, OriginalNotKeptPath, exception));
                return false;
            }
        }

        return true;
    }

    private async ValueTask<string?> ReduceAsync(
        ContentRegion region,
        string requestId,
        CcrMode mode,
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
                compressed = await executor.ExecuteAsync(part with { Ccr = mode }, cancellationToken).ConfigureAwait(false);
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
