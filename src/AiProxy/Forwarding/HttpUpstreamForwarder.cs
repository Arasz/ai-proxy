using System.Net.Http.Headers;

namespace AiProxy.Forwarding;

/// <summary>Relays a request to an upstream over HTTP, and the response back to the caller.</summary>
public sealed class HttpUpstreamForwarder : IUpstreamForwarder
{
    /// <summary>The headers that belong to one hop and never travel to the next one.</summary>
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade",
        "host",
        "content-length",
        "accept-encoding",
    };

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Holds the client factory.</summary>
    public HttpUpstreamForwarder(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Relays the request, and writes the response onto the caller's connection.</summary>
    public async Task ForwardAsync(HttpContext context, UpstreamCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(call);

        var target = new Uri(call.Upstream, call.PathAndQuery);

        using var request = new HttpRequestMessage(new HttpMethod(call.Method), target);

        if (call.Body is { } body)
        {
            request.Content = new ByteArrayContent(body.ToArray());
            if (call.ContentType is not null)
            {
                request.Content.Headers.TryAddWithoutValidation("content-type", call.ContentType);
            }
        }
        else if (call.BodyStream is { } stream)
        {
            request.Content = new StreamContent(stream);
            if (call.ContentType is not null)
            {
                request.Content.Headers.TryAddWithoutValidation("content-type", call.ContentType);
            }
        }

        CopyHeaders(call, request);

        var client = _httpClientFactory.CreateClient("upstreams");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        await RelayResponseAsync(context, response, cancellationToken);
    }

    private static void CopyHeaders(UpstreamCall call, HttpRequestMessage request)
    {
        foreach (var (name, value) in call.Headers)
        {
            if (HopByHopHeaders.Contains(name))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    private static async Task RelayResponseAsync(
        HttpContext context,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in response.Content.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }
}
