using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiProxy.Core.Doctor;
using AiProxy.Core.Gain;
using AiProxy.Storage;
using Dapper;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Integration;

/// <summary>Every loopback API route but <c>/health</c> needs the bearer token the proxy writes at each start.</summary>
public sealed class ApiTokenTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-token-{Guid.CreateVersion7():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong")]
    [InlineData("Basic Zm9vOmJhcg==")]
    public async Task Every_mapped_route_but_health_answers_401_without_the_right_token(string? authorization)
    {
        await using var proxy = await StartWithEveryRouteAsync();
        var routes = Routes(proxy);
        Assert.Contains(routes, static route => route.Path == "/gain");
        Assert.Contains(routes, static route => route.Path.StartsWith("/content/", StringComparison.Ordinal));
        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };

        foreach (var (method, path) in routes.Where(static route => route.Path != HealthApi.Path))
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (authorization is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} answered {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Health_answers_without_a_token()
    {
        await using var proxy = await StartWithEveryRouteAsync();
        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };

        using var response = await client.GetAsync(HealthApi.Path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_reset_without_the_token_deletes_nothing()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        var path = Path.Combine(proxy.DataDirectory, SqliteGainStore.FileName);
        var seed = new SqliteGainStore(new GainStoreOptions(path), TimeProvider.System);
        foreach (var id in new[] { "1", "2" })
        {
            await seed.RecordAsync(new GainHistoryEntry(id, DateTimeOffset.Parse("2026-11-02T12:00Z"), "/a", "anthropic", "POST", "log", "tool_result", "content", new GainTotals(1)), [], TestContext.Current.CancellationToken);
        }

        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        using var response = await client.PostAsync("/gain/reset", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM gain_event"));
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM gain_reset"));
    }

    [Fact]
    public async Task The_token_the_host_holds_opens_the_api()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = proxy.CreateApiClient();

        using var response = await client.GetAsync("/gain", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization?.Scheme);
    }

    private Task<ProxyHost> StartWithEveryRouteAsync()
    {
        return ProxyHost.StartAsync(new Dictionary<string, string>
        {
            ["Proxy:Compression:Enabled"] = "true",
            ["Proxy:Compression:Database"] = _databasePath
        });
    }

    private static List<(string Method, string Path)> Routes(ProxyHost proxy)
    {
        var routes = new List<(string, string)>();
        foreach (var endpoint in proxy.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var path = "/" + string.Join("/", endpoint.RoutePattern.PathSegments.Select(static segment =>
                string.Concat(segment.Parts.Select(static part => part is Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart literal ? literal.Content : "0123456789abcdef"))));
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            routes.AddRange(methods.Select(method => (method, path)));
        }

        return routes;
    }
}
