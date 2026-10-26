using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiProxy.Core.Gain;

namespace AiProxy.Client;

public interface IProxyClient
{
    Task<RetrievalResponse> RetrieveAsync(string endpoint, RetrievalRequest request, CancellationToken cancellationToken = default);
    Task<GainReport> GetGainAsync(string endpoint, GainQuery query, CancellationToken cancellationToken = default);
    Task<GainResetResult> ResetGainAsync(string endpoint, GainQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Calls the proxy's JSON API without opening its databases.</summary>
public sealed class ProxyClient(HttpClient http) : IProxyClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RetrievalResponse> RetrieveAsync(string endpoint, RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(ApiUri(endpoint, "retrieval"), request, Json, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<RetrievalResponse>(Json, cancellationToken).ConfigureAwait(false)
                     ?? throw new HttpRequestException("The proxy returned an empty retrieval response.");
        return result.Text is null ? throw new HttpRequestException("The proxy returned an invalid retrieval response.") : result;
    }

    public async Task<GainReport> GetGainAsync(string endpoint, GainQuery query, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(ApiUri(endpoint, $"gain{QueryString(query)}"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<GainReport>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new HttpRequestException("The proxy returned an empty gain response.");
    }

    public async Task<GainResetResult> ResetGainAsync(string endpoint, GainQuery query, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(ApiUri(endpoint, "gain/reset"), query, Json, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<GainResetResult>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new HttpRequestException("The proxy returned an empty reset response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = response.ReasonPhrase;
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (problem.RootElement.ValueKind == JsonValueKind.Object &&
                problem.RootElement.TryGetProperty("detail", out var value) && value.ValueKind == JsonValueKind.String)
                detail = value.GetString();
        }
        catch (JsonException)
        {
        }

        throw new HttpRequestException($"Proxy API returned {(int)response.StatusCode}: {detail}", null, response.StatusCode);
    }

    private static Uri ApiUri(string endpoint, string path)
    {
        return new Uri(new Uri($"{endpoint.TrimEnd('/')}/", UriKind.Absolute), path);
    }

    private static string QueryString(GainQuery query)
    {
        var values = new Dictionary<string, string?>
        {
            ["project"] = query.Project, ["from"] = query.From?.ToString("O", CultureInfo.InvariantCulture),
            ["to"] = query.To?.ToString("O", CultureInfo.InvariantCulture), ["provider"] = query.Provider,
            ["method"] = query.Method, ["rule"] = query.Rule, ["layer"] = query.Layer,
            ["pipeline"] = query.Pipeline, ["historyLimit"] = query.HistoryLimit.ToString(CultureInfo.InvariantCulture)
        };
        return $"?{string.Join("&", values.SelectMany(static pair => pair.Value is { } value ? [$"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value)}"] : Array.Empty<string>()))}";
    }
}
