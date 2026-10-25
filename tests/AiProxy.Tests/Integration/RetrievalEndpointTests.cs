using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Gain;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Integration;

public sealed class RetrievalEndpointTests : IAsyncDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public async Task Whole_lines_pattern_and_errors_use_retained_preview_ids_and_record_caller_recalls()
    {
        Directory.CreateDirectory(_directory.Path);
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["Proxy:Compression:Enabled"] = "true", ["Proxy:Compression:Database"] = Path.Combine(_directory.Path, "content.db"),
        });
        const string text = "first\nneedle\nthird";
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        await proxy.Services.GetRequiredService<IOriginalStore>().KeepAsync(new KeptOriginal(id, text, 3), TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = proxy.BaseAddress };
        http.DefaultRequestHeaders.Add("X-Ai-Proxy-Project", "/caller");
        foreach (var (selection, expected, error) in new (RetrievalRequest, string, string?)[]
        {
            (new(id,"whole"), text, null), (new(id,"lines",2,2), "2\tneedle", null),
            (new(id,"pattern",Pattern:"needle"), "2\tneedle", null),
            (new(id,"pattern",Pattern:"absent"), "[no line matches]", null),
            (new(id,"lines",9,9), "outside", CcrRetrieval.BadRange),
            (new("0000000000000000","whole"), "No text", CcrRetrieval.NotKept),
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
        using var reset = await http.PostAsJsonAsync("/gain/reset", new GainQuery(Project:"/caller"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK,reset.StatusCode);
        Assert.Equal(text, await proxy.Services.GetRequiredService<IOriginalStore>().FindAsync(id, TestContext.Current.CancellationToken));
        var resetGain = await http.GetFromJsonAsync<GainReport>("/gain?project=%2Fcaller", TestContext.Current.CancellationToken);
        Assert.Equal(0,resetGain!.Summary.Recalls);
    }

    public ValueTask DisposeAsync() { _directory.Dispose(); return ValueTask.CompletedTask; }
}
