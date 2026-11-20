using System.Text.Json;
using AiProxy.Configuration;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Providers;
using AiProxy.Core.Replay;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Replay;

/// <summary>Runs one stored request body through the real compression pipeline and measures each tool output region.</summary>
/// <remarks>Nothing is kept: the pipeline runs stateless and its stores discard what they are given.</remarks>
/// <param name="finder">Finds the tool output regions of a body.</param>
/// <param name="decomposer">Names the kind of content a region carries.</param>
/// <param name="pipeline">The pipeline the proxy runs.</param>
/// <param name="options">The CCR numbers the pipeline runs with.</param>
public sealed class ReplayEngine(ToolOutputRegionFinder finder, IDecompositor decomposer, ICompressionPipeline pipeline, CcrOptions options) : IDisposable
{
    private IDisposable? _owner;

    /// <summary>Builds the engine from the proxy's own service registrations, with the CCR minimum replaced when one is given.</summary>
    /// <param name="minimumTokens">The preview floor in estimated tokens, or null for the proxy's own.</param>
    public static ReplayEngine Create(int? minimumTokens)
    {
        var ccr = minimumTokens is { } minimum ? CcrOptions.Default with { MinimumTokens = minimum } : CcrOptions.Default;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddCompression(new CompressionSettings());
        services.AddSingleton(ccr);
        services.AddSingleton<IContentStore, DiscardingContentStore>();
        services.AddSingleton<IOriginalStore, DiscardingOriginalStore>();
        var provider = services.BuildServiceProvider();
        return new ReplayEngine(
            provider.GetRequiredService<ToolOutputRegionFinder>(),
            provider.GetRequiredService<IDecompositor>(),
            provider.GetRequiredService<ICompressionPipeline>(),
            ccr) { _owner = provider };
    }

    /// <summary>Replays one decoded body.</summary>
    /// <param name="body">The request body as the client sent it.</param>
    /// <param name="cancellationToken">The token that stops the replay.</param>
    public async Task<ReplayRow> ReplayAsync(byte[] body, CancellationToken cancellationToken)
    {
        if (!IsJson(body))
        {
            return ReplayRow.Of(ReplayOutcome.NotJson);
        }

        var regions = ToolOutputRegions(finder.Find(body));
        if (regions.Count == 0)
        {
            return ReplayRow.Of(ReplayOutcome.NoToolRegion);
        }

        var result = await pipeline.CompressAsync(new CompressionRequest(body, "application/json", Dialect.Unknown) { Ccr = CcrMode.Stateless }, cancellationToken).ConfigureAwait(false);
        var forwarded = result.Changed ? ToolOutputRegions(finder.Find(result.Body)) : regions;
        if (forwarded.Count != regions.Count)
        {
            return ReplayRow.Of(ReplayOutcome.Misaligned);
        }

        var blocks = regions.Select((region, index) => Block(region, forwarded[index], result.Originals)).ToArray();
        return new ReplayRow(ReplayOutcome.Replayed, blocks);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _owner?.Dispose();
    }

    private static bool IsJson(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<ContentRegion> ToolOutputRegions(IReadOnlyList<ContentRegion> regions)
    {
        return ContentCompressionPipeline.WithoutOverlaps(regions);
    }

    private ReplayBlock Block(ContentRegion original, ContentRegion forwarded, IReadOnlyDictionary<string, string> originals)
    {
        var id = forwarded.Text == original.Text
            ? null
            : originals.Where(pair => pair.Value == original.Text).Select(static pair => pair.Key).FirstOrDefault();
        return new ReplayBlock(KindOf(original), options.TokensOf(original.Text.Length), options.TokensOf(forwarded.Text.Length), id);
    }

    private ContentType KindOf(ContentRegion region)
    {
        var parts = decomposer.Decompose(region.Text, "replay", region.FilePath).Parts;
        return parts.Count == 0 ? ContentType.Unknown : parts.MaxBy(static part => part.Text.Length)!.Type;
    }

    private sealed class DiscardingContentStore : IContentStore
    {
        public ValueTask StoreAsync(ContentRecord record, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<ContentRecord?> GetAsync(string requestId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ContentRecord?>(null);
        }
    }

    private sealed class DiscardingOriginalStore : IOriginalStore
    {
        public ValueTask KeepAsync(KeptOriginal original, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> FindAsync(string id, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<string?> FindThreadAsync(string messageId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask LinkThreadAsync(string messageId, string continueFrom, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}
