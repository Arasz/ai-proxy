using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AiProxy.Core.Gain;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using AiProxy.Storage;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ZstdSharp;
using static AiProxy.Tests.Integration.TunnelClient;
using TitaniumProxyService = AiProxy.Hosting.Integration.TitaniumProxyService;

namespace AiProxy.Tests.Integration;

/// <summary>A harness-only host such as chatgpt.com: decrypted only for credentialed tunnels, and recorded, never changed.</summary>
public sealed class HarnessOnlyHostTests : IDisposable
{
    private const string CodexPath = "/backend-api/codex/responses";
    private const int ZstdDecodeFailedEventId = 1408;

    private const string ToolOutput = """
                                      2026-03-12 10:00:00 INFO worker=api-1 request started
                                      2026-03-12 10:00:01 INFO worker=api-1 request failed status=503 dur_ms=3110
                                      2026-03-12 10:00:02 INFO worker=api-1 nothing to see here
                                      2026-03-12 10:00:03 INFO worker=api-1 nothing to see here
                                      2026-03-12 10:00:04 INFO worker=api-1 nothing to see here
                                      2026-03-12 10:00:05 INFO worker=api-1 the second to last line
                                      2026-03-12 10:00:06 INFO worker=api-1 the last line
                                      """;

    private const string CompletedStream = """
                                           event: response.created
                                           data: {"type":"response.created","response":{"object":"response","usage":null}}

                                           event: response.completed
                                           data: {"type":"response.completed","response":{"object":"response","usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":800},"output_tokens":50,"total_tokens":1050}}}


                                           """;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-content-{Guid.CreateVersion7():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task A_tunnel_to_a_harness_only_host_without_a_credential_stays_opaque()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var received = ReadOriginAsync(origin, timeout.Token);
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new HarnessLoopbackHosts());
        var target = new DnsEndPoint("127.0.0.1", ((IPEndPoint)origin.LocalEndpoint).Port);
        Task? handshake = null;

        await using (var tunnel = await ConnectThroughAsync(proxy.ProxyAddress, target, null, timeout.Token))
        {
            using var tls = new SslStream(tunnel);
            try
            {
                handshake = tls.AuthenticateAsClientAsync("chatgpt.test");
                var bytes = await received;
                Assert.Equal(0x16, bytes[0]);
                Assert.Contains("chatgpt.test", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
            }
            finally
            {
                tunnel.Dispose();
                if (handshake is not null)
                {
                    await Task.WhenAny(handshake, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                    _ = handshake.Exception;
                }
            }
        }

        Assert.Empty((await ReportAsync(proxy)).History);
    }

    [Fact]
    public async Task A_websocket_upgrade_to_codex_responses_gets_426_from_the_proxy_and_never_reaches_upstream()
    {
        using var upstreamCertificate = CreateUpstreamCertificate();
        await using var upstream = await FakeUpstream.StartAsync(upstreamCertificate);
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new HarnessLoopbackHosts());
        TrustUpstream(proxy, upstreamCertificate);
        using var proxyRoot = LoadProxyRoot(proxy.DataDirectory);
        var target = new DnsEndPoint("127.0.0.1", upstream.BaseAddress.Port);
        var token = TestContext.Current.CancellationToken;

        await using var tunnel = await ConnectThroughAsync(proxy.ProxyAddress, target, Credential("/project/ws"), token);
        await using var tls = new SslStream(tunnel, false, (_, certificate, _, errors) => ChainsTo(proxyRoot, certificate as X509Certificate2, errors));
        await tls.AuthenticateAsClientAsync("127.0.0.1");
        var upgrade = $"GET {CodexPath} HTTP/1.1\r\nHost: 127.0.0.1:{target.Port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                      "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await tls.WriteAsync(Encoding.ASCII.GetBytes(upgrade), token);
        var head = await ReadHeadAsync(tls, token);

        Assert.StartsWith("HTTP/1.1 426", head, StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task A_credentialed_codex_turn_with_a_zstd_body_is_recorded_with_its_usage_and_forwarded_byte_identical()
    {
        using var upstreamCertificate = CreateUpstreamCertificate();
        await using var upstream = await FakeUpstream.StartAsync(upstreamCertificate);
        upstream.Handler = static (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            return context.Response.WriteAsync(CompletedStream);
        };
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string> { ["Proxy:Capture"] = "true" }, providerHosts: new HarnessLoopbackHosts());
        TrustUpstream(proxy, upstreamCertificate);
        using var proxyRoot = LoadProxyRoot(proxy.DataDirectory);
        var json = Encoding.UTF8.GetBytes(ResponsesBody("hello"));
        var zstd = Zstd(json);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = (context, token) => ConnectThroughAsync(proxy.ProxyAddress, context.DnsEndPoint, Credential("/project/codex"), token),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, certificate, _, errors) => ChainsTo(proxyRoot, certificate as X509Certificate2, errors) }
        };
        using var client = new HttpClient(handler);

        using var response = await client.SendAsync(ZstdPost(new Uri(upstream.BaseAddress, CodexPath), zstd), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CompletedStream, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var received = Assert.Single(upstream.Requests);
        Assert.Equal(zstd, received.BodyBytes);
        Assert.Equal("zstd", received.Headers["Content-Encoding"]);
        var row = await OnlyHistoryAsync(proxy);
        Assert.Equal(("/project/codex", "openai", GainLabels.Rules.Unchanged, GainLabels.Pipelines.Passthrough), (row.Project, row.Provider, row.Rule, row.Pipeline));
        Assert.Equal((json.LongLength, json.LongLength), (row.Totals.OriginalCharacters, row.Totals.ForwardedCharacters));
        Assert.Equal(json, Assert.Single(await ReadCapturedBodiesAsync(proxy)));
        Assert.Equal((200L, 50L, (long?)null, 800L, (string?)null), await ReadUsageAsync(proxy));
    }

    [Fact]
    public async Task A_harness_only_body_is_forwarded_byte_identical_and_recorded_unchanged_with_compression_on()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(
            new Dictionary<string, string> { ["Proxy:Compression:Enabled"] = "true", ["Proxy:Compression:Database"] = _databasePath },
            providerHosts: new HarnessLoopbackHosts());
        var body = Encoding.UTF8.GetBytes(ResponsesBody(ToolOutput));

        using var response = await PlainPostAsync(proxy, upstream, new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, Assert.Single(upstream.Requests).BodyBytes);
        var row = await OnlyHistoryAsync(proxy);
        Assert.Equal((GainLabels.Rules.Unchanged, GainLabels.Pipelines.Passthrough), (row.Rule, row.Pipeline));
    }

    [Fact]
    public async Task A_compressing_host_still_reduces_the_same_body_so_the_record_only_check_can_fail()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(
            new Dictionary<string, string> { ["Proxy:Compression:Enabled"] = "true", ["Proxy:Compression:Database"] = _databasePath },
            providerHosts: new LoopbackProviderHosts(Dialect.OpenAi));
        var body = Encoding.UTF8.GetBytes(ResponsesBody(ToolOutput));

        using var response = await PlainPostAsync(proxy, upstream, new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } });

        Assert.NotEqual(body, Assert.Single(upstream.Requests).BodyBytes);
    }

    [Fact]
    public async Task A_zstd_body_that_does_not_decode_is_forwarded_unchanged_logged_and_recorded_as_sent()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var logs = new RecordingLoggerProvider();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new HarnessLoopbackHosts(), logging: logs);
        byte[] garbage = [0x28, 0xB5, 0x2F, 0xFD, 0xFF, 0x00, 0x13, 0x37, 0x42, 0x42, 0x42];

        using var response = await PlainPostAsync(proxy, upstream, ZstdContent(garbage));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(garbage, Assert.Single(upstream.Requests).BodyBytes);
        Assert.Contains(logs.Entries, entry => entry.EventId == ZstdDecodeFailedEventId);
        var report = await ReportAsync(proxy);
        Assert.Equal(GainLabels.Rules.Unchanged, (await OnlyHistoryAsync(proxy)).Rule);
        Assert.Contains(report.Failures, failure => failure.Stage == "zstd");
    }

    private static string ResponsesBody(string output)
    {
        return $$"""{"model":"gpt-5","stream":true,"input":[{"type":"function_call_output","call_id":"call_1","output":{{JsonSerializer.Serialize(output)}}}]}""";
    }

    private static byte[] Zstd(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var encoder = new CompressionStream(output, 3, leaveOpen: true))
        {
            encoder.Write(bytes);
        }

        return output.ToArray();
    }

    private static ByteArrayContent ZstdContent(byte[] encoded)
    {
        var content = new ByteArrayContent(encoded);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("zstd");
        return content;
    }

    private static HttpRequestMessage ZstdPost(Uri uri, byte[] encoded)
    {
        return new HttpRequestMessage(HttpMethod.Post, uri) { Content = ZstdContent(encoded) };
    }

    private static async Task<HttpResponseMessage> PlainPostAsync(ProxyHost proxy, FakeUpstream upstream, HttpContent content)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        return await client.PostAsync(new Uri(upstream.BaseAddress, CodexPath), content, TestContext.Current.CancellationToken);
    }

    private static void TrustUpstream(ProxyHost proxy, X509Certificate2 upstreamCertificate)
    {
        proxy.Services.GetRequiredService<TitaniumProxyService>().Server.ServerCertificateValidationCallback += (_, e) =>
        {
            e.IsValid = e.Certificate is not null && e.Certificate.GetCertHashString() == upstreamCertificate.GetCertHashString();
            return Task.CompletedTask;
        };
    }

    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken token)
    {
        using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        headTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, headTimeout.Token) == 0)
            {
                break;
            }

            head.Append((char)one[0]);
        }

        return head.ToString();
    }

    private static async Task<GainReport> ReportAsync(ProxyHost proxy)
    {
        using var api = new HttpClient { BaseAddress = proxy.BaseAddress };
        return (await api.GetFromJsonAsync<GainReport>("/gain", TestContext.Current.CancellationToken))!;
    }

    private static async Task<GainHistoryEntry> OnlyHistoryAsync(ProxyHost proxy)
    {
        for (var attempt = 0; ; attempt++)
        {
            var report = await ReportAsync(proxy);
            if (report.History.Count == 1 || attempt == 40)
            {
                return Assert.Single(report.History);
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<IReadOnlyList<byte[]>> ReadCapturedBodiesAsync(ProxyHost proxy)
    {
        await using var connection = await OpenCaptureAsync(proxy);
        return [.. await connection.QueryAsync<byte[]>("SELECT body FROM capture_request;")];
    }

    /// <summary>The response row is written after the client has the response, so this polls for it.</summary>
    private static async Task<(long?, long?, long?, long?, string?)> ReadUsageAsync(ProxyHost proxy)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var connection = await OpenCaptureAsync(proxy);
            var rows = (await connection.QueryAsync<(long?, long?, long?, long?, string?)>(
                "SELECT input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, usage_error FROM capture_response;")).ToList();
            if (rows.Count == 1 || attempt == 100)
            {
                return Assert.Single(rows);
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<SqliteConnection> OpenCaptureAsync(ProxyHost proxy)
    {
        var path = Path.Combine(proxy.DataDirectory, SqliteCaptureStore.FileName);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    /// <summary>Treats loopback as a harness-only OpenAI host, the way the catalog treats chatgpt.com.</summary>
    private sealed class HarnessLoopbackHosts : IProviderHosts
    {
        public IReadOnlyCollection<string> Hosts { get; } = ["127.0.0.1", "localhost", "::1"];

        public Dialect? Find(string host)
        {
            return IsHarnessOnly(host) ? Dialect.OpenAi : null;
        }

        public bool IsHarnessOnly(string host)
        {
            return host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
        }
    }
}
