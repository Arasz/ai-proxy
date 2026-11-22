using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiProxy.Core.Gain;
using AiProxy.Hosting.Api;

namespace AiProxy.Client;

/// <summary>Where the proxy's API answers and the bearer token it expects; a null token sends no Authorization header.</summary>
/// <param name="Endpoint">The API base URL.</param>
/// <param name="Token">The token from the proxy's data directory, or null when there is none.</param>
/// <param name="TokenFile">The token file the client read, named when the API refuses the token.</param>
public sealed record ProxyApi(string Endpoint, string? Token, string TokenFile)
{
    /// <summary>The API at <paramref name="endpoint" /> with the token read from <paramref name="dataDirectory" />.</summary>
    public static ProxyApi For(string endpoint, string dataDirectory)
    {
        return new ProxyApi(endpoint, ApiToken.Read(dataDirectory), Path.Combine(dataDirectory, ApiToken.FileName));
    }
}

public interface IProxyClient
{
    Task<RetrievalResponse> RetrieveAsync(ProxyApi api, RetrievalRequest request, CancellationToken cancellationToken = default);
    Task<GainReport> GetGainAsync(ProxyApi api, GainQuery query, CancellationToken cancellationToken = default);
    Task<GainResetResult> ResetGainAsync(ProxyApi api, GainQuery query, string caller, CancellationToken cancellationToken = default);
}

/// <summary>Calls the proxy's JSON API without opening its databases.</summary>
public sealed class ProxyClient(HttpClient http) : IProxyClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RetrievalResponse> RetrieveAsync(ProxyApi api, RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        using var message = Request(api, HttpMethod.Post, "retrieval");
        message.Content = JsonContent.Create(request, options: Json);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(api, response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<RetrievalResponse>(Json, cancellationToken).ConfigureAwait(false)
                     ?? throw new HttpRequestException("The proxy returned an empty retrieval response.");
        return result.Text is null ? throw new HttpRequestException("The proxy returned an invalid retrieval response.") : result;
    }

    public async Task<GainReport> GetGainAsync(ProxyApi api, GainQuery query, CancellationToken cancellationToken = default)
    {
        using var message = Request(api, HttpMethod.Get, $"gain{QueryString(query)}");
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(api, response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<GainReport>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new HttpRequestException("The proxy returned an empty gain response.");
    }

    public async Task<GainResetResult> ResetGainAsync(ProxyApi api, GainQuery query, string caller, CancellationToken cancellationToken = default)
    {
        using var request = Request(api, HttpMethod.Post, "gain/reset");
        request.Content = JsonContent.Create(query, options: Json);
        request.Headers.TryAddWithoutValidation(GainApi.CallerHeader, caller);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(api, response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<GainResetResult>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new HttpRequestException("The proxy returned an empty reset response.");
    }

    private static async Task EnsureSuccessAsync(ProxyApi api, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException(ApiToken.Refused(api.TokenFile, api.Token is not null), null, response.StatusCode);
        }

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
        catch (JsonException)
        {
        }

        throw new HttpRequestException($"Proxy API returned {(int)response.StatusCode}: {detail}", null, response.StatusCode);
    }

    private static HttpRequestMessage Request(ProxyApi api, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, ApiUri(api.Endpoint, path));
        if (api.Token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.Token);
        }

        return request;
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