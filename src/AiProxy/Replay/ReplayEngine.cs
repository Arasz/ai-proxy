using System.Text.Json;
using AiProxy.Configuration;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Providers;
using AiProxy.Core.Replay;

namespace AiProxy.Replay;

/// <summary>Runs one stored request body through the real compression pipeline twice and measures each tool output region.</summary>
/// <remarks>
///     The first run has the preview floor out of reach, so what it removes is the reducers' work, code views included. The
///     second run has the real floor, so what it removes on top is the previews'. Nothing is kept: the pipeline runs stateless
///     and its stores discard what they are given.
/// </remarks>
/// <param name="finder">Finds the tool output regions of a body.</param>
/// <param name="decomposer">Names the kind of content a region carries.</param>
/// <param name="reducers">The pipeline with previews out of reach.</param>
/// <param name="previews">The pipeline the proxy runs, with the floor being tested.</param>
/// <param name="options">The CCR numbers the previews pipeline runs with.</param>
public sealed class ReplayEngine(ToolOutputRegionFinder finder, IDecompositor decomposer, ICompressionPipeline reducers, ICompressionPipeline previews, CcrOptions options) : IDisposable
{
    private readonly List<IDisposable> _owners = [];

    /// <summary>Builds the engine from the proxy's own service registrations, with the CCR minimum replaced when one is given.</summary>
    /// <param name="minimumTokens">The preview floor in estimated tokens, or null for the proxy's own.</param>
    /// <param name="budgetShare">The share of original tokens a preview may hold, or null for the proxy's own.</param>
    /// <param name="minimumSavingTokens">The least a preview must save, or null for the proxy's own.</param>
    /// <param name="skipUnderTokens">The skip band for numbered reads and diffs in estimated tokens, or null for the proxy's own.</param>
    /// <param name="configuration">The configuration the compression settings are bound from, as the proxy host binds them.</param>
    public static ReplayEngine Create(int? minimumTokens, decimal? budgetShare, int? minimumSavingTokens, int? skipUnderTokens, IConfiguration configuration)
    {
        var ccr = CcrOptions.Default with
        {
            MinimumTokens = minimumTokens ?? CcrOptions.Default.MinimumTokens,
            BudgetShare = budgetShare ?? CcrOptions.Default.BudgetShare,
            MinimumSavingTokens = minimumSavingTokens ?? CcrOptions.Default.MinimumSavingTokens,
            SkipUnderTokens = skipUnderTokens ?? CcrOptions.Default.SkipUnderTokens
        };
        var settings = ProxySettings.Load(configuration).Compression;
        var withoutPreviews = Build(settings, ccr with { MinimumTokens = int.MaxValue });
        var withPreviews = Build(settings, ccr);
        return new ReplayEngine(
            withPreviews.GetRequiredService<ToolOutputRegionFinder>(),
            withPreviews.GetRequiredService<IDecompositor>(),
            withoutPreviews.GetRequiredService<ICompressionPipeline>(),
            withPreviews.GetRequiredService<ICompressionPipeline>(),
            ccr) { _owners = { withoutPreviews, withPreviews } };
    }

    private static ServiceProvider Build(CompressionSettings settings, CcrOptions ccr)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddCompressionPipeline(settings);
        services.AddSingleton(ccr);
        services.AddSingleton<IContentStore, DiscardingContentStore>();
        services.AddSingleton<IOriginalStore, DiscardingOriginalStore>();
        return services.BuildServiceProvider();
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

        var reducedResult = await CompressAsync(reducers, body, cancellationToken).ConfigureAwait(false);
        var previewResult = await CompressAsync(previews, body, cancellationToken).ConfigureAwait(false);
        var reduced = RegionsAfter(reducedResult, regions);
        var forwarded = RegionsAfter(previewResult, regions);
        if (!Aligned(regions, reduced) || !Aligned(regions, forwarded))
        {
            return ReplayRow.Of(ReplayOutcome.Misaligned);
        }

        var blocks = regions.Select((region, index) => Block(region, reduced[index], forwarded[index], previewResult.Originals)).ToArray();
        return new ReplayRow(ReplayOutcome.Replayed, blocks);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var owner in _owners)
        {
            owner.Dispose();
        }
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

    private static bool Aligned(IReadOnlyList<ContentRegion> original, IReadOnlyList<ContentRegion> after)
    {
        return after.Count == original.Count && after.Zip(after.Skip(1)).All(static pair => pair.First.Start < pair.Second.Start);
    }

    private static ValueTask<CompressionResult> CompressAsync(ICompressionPipeline pipeline, byte[] body, CancellationToken cancellationToken)
    {
        return pipeline.CompressAsync(new CompressionRequest(body, "application/json", Dialect.Unknown) { Ccr = CcrMode.Stateless }, cancellationToken);
    }

    private IReadOnlyList<ContentRegion> RegionsAfter(CompressionResult result, IReadOnlyList<ContentRegion> unchanged)
    {
        return result.Changed ? ToolOutputRegions(finder.Find(result.Body)) : unchanged;
    }

    private ReplayBlock Block(ContentRegion original, ContentRegion reduced, ContentRegion forwarded, IReadOnlyDictionary<string, string> originals)
    {
        var id = forwarded.Text == reduced.Text
            ? null
            : originals.Where(pair => pair.Value == original.Text).Select(static pair => pair.Key).FirstOrDefault() ?? "unidentified";
        return new ReplayBlock(KindOf(original), Tokens(original), Tokens(reduced), Tokens(forwarded), id) { Skipped = id is null && WasSkipped(original, Tokens(reduced)) };
    }

    private bool WasSkipped(ContentRegion original, int reducedTokens)
    {
        return reducedTokens >= options.MinimumTokens && options.SkipsPreview(original.Text, reducedTokens);
    }

    private int Tokens(ContentRegion region)
    {
        return options.TokensOf(region.Text.Length);
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
