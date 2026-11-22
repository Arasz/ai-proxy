using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiProxy.Core.Gain;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Tests.Integration;

public sealed class GainEndpointTests
{
    [Fact]
    public async Task Gain_is_available_without_raw_capture_or_compression()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = proxy.CreateApiClient();
        using var response = await client.GetAsync("/gain", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("estimated", report.RootElement.GetProperty("measurement").GetProperty("kind").GetString());
        Assert.Equal(0, report.RootElement.GetProperty("summary").GetProperty("requests").GetInt64());
    }

    [Theory]
    [InlineData("from=garbage")]
    [InlineData("from=2026-12-02&to=2026-12-01")]
    [InlineData("from=2026-12-01&to=2026-12-01")]
    [InlineData("historyLimit=-1")]
    [InlineData("historyLimit=1001")]
    [InlineData("historyLimit=bad")]
    [InlineData("project=%20")]
    public async Task Invalid_filters_return_problem_details(string filter)
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = proxy.CreateApiClient();
        using var response = await client.GetAsync($"/gain?{filter}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("evil.test")]
    [InlineData("evil.test:17432")]
    public async Task A_request_named_for_another_host_is_refused_so_a_rebound_page_cannot_read_gain_or_originals(string host)
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = proxy.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/gain");
        request.Headers.Host = host;

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task A_request_named_for_the_loopback_host_is_served(string host)
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = proxy.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/gain");
        request.Headers.Host = $"{host}:{proxy.BaseAddress.Port}";

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_reset_over_http_leaves_an_audit_row_with_the_filter_the_count_and_the_caller_the_client_sent()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        var path = Path.Combine(proxy.DataDirectory, "gain.db");
        var at = DateTimeOffset.Parse("2026-11-02T12:00Z");
        var seed = new SqliteGainStore(new GainStoreOptions(path), TimeProvider.System);
        foreach (var (id, project) in new[] { ("1", "/a"), ("2", "/a"), ("3", "/b") })
        {
            await seed.RecordAsync(new GainHistoryEntry(id, at, project, "anthropic", "POST", "log", "tool_result", "content", new GainTotals(1)), [], TestContext.Current.CancellationToken);
        }

        using var client = proxy.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/gain/reset") { Content = JsonContent.Create(new GainQuery("/a")) };
        request.Headers.Add(GainApi.CallerHeader, "cli gain --reset pid 1234");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var audit = await connection.QuerySingleAsync<(long Removed, string Filter, string Caller)>("SELECT removed,filter,caller FROM gain_reset");
        Assert.Equal((2L, "cli gain --reset pid 1234"), (audit.Removed, audit.Caller));
        Assert.Equal("/a", JsonSerializer.Deserialize<GainQuery>(audit.Filter)!.Project);
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM gain_event"));
    }

    [Fact]
    public async Task A_reset_without_a_caller_is_recorded_with_the_caller_empty()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = proxy.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/gain/reset", new GainQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(proxy.DataDirectory, "gain.db")};Mode=ReadOnly");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("", await connection.ExecuteScalarAsync<string>("SELECT caller FROM gain_reset"));
    }
}