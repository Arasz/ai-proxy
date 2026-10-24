using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Integration;

/// <summary>What Titanium's DataReceived hands the proxy while a response streams through it.</summary>
public sealed class ResponseDataTests
{
    [Fact]
    public async Task DataReceived_delivers_the_response_body_de_chunked_and_still_content_encoded()
    {
        var encoded = Gzip("""{"usage":{"input_tokens":12,"output_tokens":34}}"""u8.ToArray());
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static async (_, context) =>
        {
            // No Content-Length and two flushes: Kestrel sends the body chunked.
            context.Response.Headers.ContentEncoding = "gzip";
            context.Response.ContentType = "application/json";
            var bytes = Gzip("""{"usage":{"input_tokens":12,"output_tokens":34}}"""u8.ToArray());
            await context.Response.Body.WriteAsync(bytes.AsMemory(0, 10));
            await context.Response.Body.FlushAsync();
            await context.Response.Body.WriteAsync(bytes.AsMemory(10));
        };
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new LoopbackProviderHosts(Dialect.Anthropic));
        using var received = new MemoryStream();
        string? transferEncoding = null;
        var server = proxy.Services.GetRequiredService<TitaniumProxyService>().Server;
        server.BeforeRequest += (_, e) =>
        {
            e.DataReceived += (_, data) =>
            {
                lock (received)
                {
                    received.Write(data.Buffer, data.Offset, data.Count);
                }
            };
            return Task.CompletedTask;
        };
        server.BeforeResponse += (_, e) =>
        {
            transferEncoding = e.HttpClient.Response.Headers.GetFirstHeader("Transfer-Encoding")?.Value;
            return Task.CompletedTask;
        };

        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true, AutomaticDecompression = DecompressionMethods.None };
        using var client = new HttpClient(handler);
        using var content = new ByteArrayContent("""{"model":"m"}"""u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(new Uri(upstream.BaseAddress, "/v1/messages"), content, TestContext.Current.CancellationToken);
        var clientBytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal("chunked", transferEncoding);
        Assert.Equal(encoded, clientBytes);
        Assert.Equal(encoded, received.ToArray());
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
    }
}
