using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiProxy.Core.Gain;

namespace AiProxy.Client;

/// <summary>The shared loopback endpoint used by the CLI and MCP adapters.</summary>
public static class ProxyClientOptions
{
    public const string DefaultEndpoint = "http://127.0.0.1:17432";
    public const string ProjectHeader = "X-Ai-Proxy-Project";
}

/// <summary>Calls the proxy's JSON API without opening its databases.</summary>
public sealed class ProxyClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static HttpClient CreateHttpClient(string endpoint = ProxyClientOptions.DefaultEndpoint) =>
        new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };

    public async Task<RetrievalResponse> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("retrieval", request, Json, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RetrievalResponse>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The proxy returned an empty retrieval response.");
    }

    public async Task<GainReport> GetGainAsync(GainQuery query, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("gain" + QueryString(query), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<GainReport>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The proxy returned an empty gain response.");
    }

    public async Task<GainResetResult> ResetGainAsync(GainQuery query, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("gain/reset", query, Json, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<GainResetResult>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The proxy returned an empty reset response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) { return; }
        var detail = response.ReasonPhrase;
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (problem.RootElement.ValueKind == JsonValueKind.Object &&
                problem.RootElement.TryGetProperty("detail", out var value) && value.ValueKind == JsonValueKind.String)
            {
                detail = value.GetString();
            }
        }
        catch (JsonException) { }
        throw new HttpRequestException($"Proxy API returned {(int)response.StatusCode}: {detail}", null, response.StatusCode);
    }

    private static string QueryString(GainQuery query)
    {
        var values = new Dictionary<string, string?>
        {
            ["project"] = query.Project, ["from"] = query.From?.ToString("O", CultureInfo.InvariantCulture),
            ["to"] = query.To?.ToString("O", CultureInfo.InvariantCulture), ["provider"] = query.Provider,
            ["method"] = query.Method, ["rule"] = query.Rule, ["layer"] = query.Layer,
            ["pipeline"] = query.Pipeline, ["historyLimit"] = query.HistoryLimit.ToString(CultureInfo.InvariantCulture),
        };
        return "?" + string.Join("&", values.Where(static pair => pair.Value is not null)
            .Select(static pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value!)));
    }
}
