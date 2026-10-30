using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Gain;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Integration;

public sealed class RetrievalEndpointTests : IAsyncDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public ValueTask DisposeAsync()
    {
        _directory.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Whole_lines_pattern_and_errors_use_retained_preview_ids_and_record_caller_recalls()
    {
        Directory.CreateDirectory(_directory.Path);
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["Proxy:Compression:Enabled"] = "true", ["Proxy:Compression:Database"] = Path.Combine(_directory.Path, "content.db")
        });
        const string text = "first\nneedle\nthird";
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        await proxy.Services.GetRequiredService<IOriginalStore>().KeepAsync(new KeptOriginal(id, text, 3), TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = proxy.BaseAddress };
        http.DefaultRequestHeaders.Add("X-Ai-Proxy-Project", "/caller");
        foreach (var (selection, expected, error) in new[]
                 {
                     (new RetrievalRequest(id, "whole"), text, null), (new RetrievalRequest(id, "lines", 2, 2), "2\tneedle", null),
                     (new RetrievalRequest(id, "pattern", Pattern: "needle"), "2\tneedle", null),
                     (new RetrievalRequest(id, "pattern", Pattern: "absent"), "[no line matches]", null),
                     (new RetrievalRequest(id, "lines", 9, 9), "outside", CcrRetrieval.BadRange),
                     (new RetrievalRequest("0000000000000000", "whole"), "No text", CcrRetrieval.NotKept)
                 })
        {
            using var response = await http.PostAsJsonAsync("/retrieval", selection, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var answer = await response.Content.ReadFromJsonAsync<RetrievalResponse>(TestContext.Current.CancellationToken);
            Assert.Contains(expected, answer!.Text, StringComparison.Ordinal);
            Assert.Equal(error, answer.Error);
        }

        var gain = await http.GetFromJsonAsync<GainReport>("/gain?project=%2Fcaller", TestContext.Current.CancellationToken);
        Assert.Equal(6, gain!.Summary.Recalls);
        Assert.All(gain.Recalls, recall => Assert.Equal("caller", recall.Attribution));
        Assert.True(gain.Summary.EstimatedRetrievedTokens > 0);
        using var reset = await http.PostAsJsonAsync("/gain/reset", new GainQuery("/caller"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(text, await proxy.Services.GetRequiredService<IOriginalStore>().FindAsync(id, TestContext.Current.CancellationToken));
        var resetGain = await http.GetFromJsonAsync<GainReport>("/gain?project=%2Fcaller", TestContext.Current.CancellationToken);
        Assert.Equal(0, resetGain!.Summary.Recalls);
    }

    [Fact]
    public async Task A_recall_the_ledger_cannot_write_still_returns_the_original()
    {
        Directory.CreateDirectory(_directory.Path);
        await using var proxy = await ProxyHost.StartAsync(
            new Dictionary<string, string>
            {
                ["Proxy:Compression:Enabled"] = "true", ["Proxy:Compression:Database"] = Path.Combine(_directory.Path, "content.db")
            },
            configure: services => services.AddSingleton<IGainStore, FailingGainStore>());
        const string text = "only line";
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        await proxy.Services.GetRequiredService<IOriginalStore>().KeepAsync(new KeptOriginal(id, text, 1), TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = proxy.BaseAddress };

        using var response = await http.PostAsJsonAsync("/retrieval", new RetrievalRequest(id, "whole"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<RetrievalResponse>(TestContext.Current.CancellationToken);
        Assert.EndsWith(text, answer?.Text ?? "", StringComparison.Ordinal);
    }

    private sealed class FailingGainStore : IGainStore
    {
        public ValueTask RecordAsync(GainHistoryEntry entry, IReadOnlyList<GainFailure> failures, CancellationToken cancellationToken)
        {
            throw new IOException("disk full");
        }

        public ValueTask RecordOtherAsync(GainOtherEntry entry, CancellationToken cancellationToken)
        {
            throw new IOException("disk full");
        }

        public ValueTask RecallAsync(GainRecall recall, string? provider, string method, string rule, string layer, string pipeline, CancellationToken cancellationToken)
        {
            throw new IOException("disk full");
        }

        public ValueTask<GainReport> QueryAsync(GainQuery query, CancellationToken cancellationToken)
        {
            throw new IOException("disk full");
        }

        public ValueTask<GainResetResult> ResetAsync(GainQuery query, CancellationToken cancellationToken)
        {
            throw new IOException("disk full");
        }
    }
}