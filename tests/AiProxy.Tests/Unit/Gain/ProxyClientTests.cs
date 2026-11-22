using System.Net;
using System.Text;
using System.Text.Json;
using AiProxy.Client;
using AiProxy.Core.Gain;

namespace AiProxy.Tests.Unit.Gain;

public sealed class ProxyClientTests
{
    [Fact]
    public async Task Optional_retrieval_arguments_are_omitted_and_filters_round_trip_without_shell_or_query_injection()
    {
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            if (request.RequestUri!.AbsolutePath == "/retrieval")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                Assert.False(json.RootElement.TryGetProperty("start", out _));
                Assert.False(json.RootElement.TryGetProperty("end", out _));
                Assert.False(json.RootElement.TryGetProperty("pattern", out _));
                Assert.Equal("whole", json.RootElement.GetProperty("mode").GetString());
                return Json("{\"text\":\"original\",\"error\":null}");
            }

            Assert.Contains("project=%2Fa%26b", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("historyLimit=7", request.RequestUri.Query, StringComparison.Ordinal);
            return Json(JsonSerializer.Serialize(GainReportBuilder.Build(new GainQuery(), DateTimeOffset.UnixEpoch, [], [], []), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        })) { BaseAddress = new Uri("http://127.0.0.1:1") };
        var client = new ProxyClient(http);
        var endpoint = new ProxyApi("http://127.0.0.1:1", null);
        Assert.Equal("original", (await client.RetrieveAsync(endpoint, new RetrievalRequest("0123456789abcdef", "whole"), TestContext.Current.CancellationToken)).Text);
        Assert.Equal(0, (await client.GetGainAsync(endpoint, new GainQuery("/a&b", HistoryLimit: 7), TestContext.Current.CancellationToken)).Summary.Requests);
    }

    [Fact]
    public async Task Api_problem_is_an_actionable_failure_instead_of_an_empty_success_report()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"title\":\"invalid_gain_filter\",\"detail\":\"from must be earlier than to.\"}")
        }))) { BaseAddress = new Uri("http://127.0.0.1:1") };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new ProxyClient(http).GetGainAsync(new ProxyApi("http://127.0.0.1:1", null), new GainQuery(), TestContext.Current.CancellationToken));
        Assert.Contains("from must be earlier than to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_call_sends_the_token_as_a_bearer_header_and_none_without_one()
    {
        var seen = new List<string?>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            seen.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/retrieval" => Json("{\"text\":\"original\",\"error\":null}"),
                "/gain/reset" => Json("{\"removed\":0,\"cutoff\":\"2026-11-01T00:00:00Z\"}"),
                _ => Json(JsonSerializer.Serialize(GainReportBuilder.Build(new GainQuery(), DateTimeOffset.UnixEpoch, [], [], []), new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            });
        }));
        var client = new ProxyClient(http);
        var api = new ProxyApi("http://127.0.0.1:1", "abc");

        await client.RetrieveAsync(api, new RetrievalRequest("0123456789abcdef", "whole"), TestContext.Current.CancellationToken);
        await client.GetGainAsync(api, new GainQuery(), TestContext.Current.CancellationToken);
        await client.ResetGainAsync(api, new GainQuery(), "test", TestContext.Current.CancellationToken);
        await client.GetGainAsync(api with { Token = null }, new GainQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(["Bearer abc", "Bearer abc", "Bearer abc", null], seen);
    }

    private static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return send(request, cancellationToken);
        }
    }
}