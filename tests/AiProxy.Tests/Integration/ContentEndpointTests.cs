using System.Net;
using AiProxy.Core.Content;
using AiProxy.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Integration;

public sealed class ContentEndpointTests : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-content-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task A_stored_original_is_served_and_an_unknown_id_returns_not_found()
    {
        using (var connection = ContentSchema.Open(_databasePath))
        {
            ContentSchema.Ensure(connection);
        }

        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["Proxy:Compression:Enabled"] = "true",
            ["Proxy:Compression:Database"] = _databasePath,
        });
        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        var store = proxy.Services.GetRequiredService<IContentStore>();
        const string requestId = "request-1";
        await store.StoreAsync(
            new ContentRecord(requestId, DateTimeOffset.UtcNow, "original body", "compressed body", 0.5, "test", 0, []),
            TestContext.Current.CancellationToken);

        var original = await client.GetStringAsync($"/content/{requestId}", TestContext.Current.CancellationToken);
        using var missing = await client.GetAsync("/content/missing", TestContext.Current.CancellationToken);

        Assert.Equal("original body", original);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            File.Delete(file);
        }

        return ValueTask.CompletedTask;
    }
}
