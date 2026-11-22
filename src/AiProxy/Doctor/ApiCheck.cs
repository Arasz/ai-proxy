using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AiProxy.Core.Doctor;
using AiProxy.Hosting.Api;

namespace AiProxy.Doctor;

/// <summary>The api check's result and whether anything answered at the endpoint.</summary>
internal sealed record ApiProbe(CheckResult Result, bool Reachable);

/// <summary>Asks the endpoint's <c>/health</c> who answers and which version it is, then whether the API accepts the token from the data directory.</summary>
internal sealed class ApiCheck(HttpClient http, Uri endpoint, string version, string dataDirectory)
{
    public const string Name = "api";

    public async Task<ApiProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var shown = EndpointText.Safe(endpoint.ToString());
        string body;
        try
        {
            using var response = await http.GetAsync(new Uri(endpoint, HealthApi.Path), cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
        {
            return new ApiProbe(
                new CheckResult(Name, CheckStatus.Fail, $"No answer from {shown}.", "Start ai-proxy, or pass --endpoint with the right address."),
                false);
        }

        var result = Judge(shown, body);
        return new ApiProbe(result.Status is CheckStatus.Pass or CheckStatus.Warn ? await CheckTokenAsync(shown, result, cancellationToken).ConfigureAwait(false) : result, true);
    }

    private async Task<CheckResult> CheckTokenAsync(string shown, CheckResult answered, CancellationToken cancellationToken)
    {
        string? token;
        try
        {
            token = ApiToken.Read(dataDirectory);
        }
        catch (IOException exception)
        {
            return new CheckResult(Name, CheckStatus.Fail, $"ai-proxy answers at {shown}, but: {exception.Message}", "Fix the token file's owner or mode, or restart ai-proxy so it writes a new one.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/gain?historyLimit=1"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.Unauthorized
                ? new CheckResult(Name, CheckStatus.Fail, $"ai-proxy answers at {shown}, but: {ApiToken.Refused(Path.Combine(dataDirectory, ApiToken.FileName), token is not null)}", "Pass --data-directory with the directory the running proxy uses.")
                : answered;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
        {
            return answered;
        }
    }

    private CheckResult Judge(string shown, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Foreign(shown);
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("product", out var product))
            {
                return root.TryGetProperty("status", out _)
                    ? new CheckResult(Name, CheckStatus.Unknown, $"{shown} answers but does not say which product or version it is; it is probably an older ai-proxy.", "Restart ai-proxy on the current version.")
                    : Foreign(shown);
            }

            if (product.ValueKind != JsonValueKind.String || product.GetString() != HealthApi.Product)
            {
                return Foreign(shown);
            }

            var running = root.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return running == version
                ? new CheckResult(Name, CheckStatus.Pass, $"ai-proxy {running} answers at {shown}.")
                : new CheckResult(Name, CheckStatus.Warn, $"ai-proxy {running ?? "(unknown version)"} answers at {shown}, but this command is {version}.", "Restart ai-proxy so the running proxy and the command match.");
        }
        catch (JsonException)
        {
            return Foreign(shown);
        }
    }

    private static CheckResult Foreign(string shown) => new(Name, CheckStatus.Fail, $"{shown} answers, but not as ai-proxy.", "Pass --endpoint with the address ai-proxy listens on.");
}
