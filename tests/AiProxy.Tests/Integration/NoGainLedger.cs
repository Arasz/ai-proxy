using AiProxy.Core.Gain;

namespace AiProxy.Tests.Integration;

/// <summary>A ledger that records nothing, for tests that build the inspector or relay by hand.</summary>
internal sealed class NoGainLedger : IGainStore
{
    public ValueTask RecordAsync(GainHistoryEntry entry, IReadOnlyList<GainFailure> failures, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask RecallAsync(GainRecall recall, string? provider, string method, string rule, string layer, string pipeline, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<GainReport> QueryAsync(GainQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GainReportBuilder.Build(query, TimeProvider.System.GetUtcNow(), [], [], []));

    public ValueTask<GainResetResult> ResetAsync(GainQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new GainResetResult(0, TimeProvider.System.GetUtcNow()));
}
