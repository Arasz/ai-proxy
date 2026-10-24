using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiProxy.Core.Capture;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using AiProxy.Storage;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Integration;

/// <summary>Capture mode through the real host: what the upstream receives and what the capture file holds.</summary>
public sealed class RequestCaptureTests : IDisposable
{
    private const string ToolOutput = """
        2026-03-12 10:00:00 INFO worker=api-1 request started
        2026-03-12 10:00:01 INFO worker=api-1 request failed status=503 dur_ms=3110
        2026-03-12 10:00:02 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:03 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:04 INFO worker=api-1 nothing to see here
        2026-03-12 10:00:05 INFO worker=api-1 the second to last line
        2026-03-12 10:00:06 INFO worker=api-1 the last line
        """;

    private const int CaptureFailedEventId = 1403;

    private readonly string _contentDatabasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-content-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _contentDatabasePath, $"{_contentDatabasePath}-wal", $"{_contentDatabasePath}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task Capture_forwards_the_body_byte_for_byte_and_keeps_it_with_where_it_went()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 30, 15, 250, TimeSpan.Zero);
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(capture: true, time: new FakeTimeProvider(now));
        // Multi-byte characters and odd spacing: a re-serialized or re-encoded body would differ.
        var body = Encoding.UTF8.GetBytes("{ \"model\" :\"m\",\t\"messages\":[{\"role\":\"user\",\"content\":\"zażółć gęślą jaźń \\u00e9 ☃\"}]}  ");

        using var response = await SendAsync(proxy, upstream, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, Assert.Single(upstream.Requests).BodyBytes);
        var row = Assert.Single(await ReadCapturesAsync(proxy));
        Assert.Equal(body, row.Body);
        Assert.Equal(nameof(Dialect.Anthropic), row.Provider);
        Assert.Equal(upstream.BaseAddress.Host, row.Host);
        Assert.Equal("/v1/messages", row.Path);
        Assert.False(string.IsNullOrWhiteSpace(row.RequestId));
        var capturedAt = DateTimeOffset.Parse(row.CapturedAt, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(now, capturedAt);
    }

    [Fact]
    public async Task With_compression_on_the_capture_holds_the_body_before_compression()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(capture: true, compression: true);
        var body = ToolResultBody();

        using var response = await SendAsync(proxy, upstream, body);

        Assert.NotEqual(body, Assert.Single(upstream.Requests).BodyBytes);
        Assert.Equal(body, Assert.Single(await ReadCapturesAsync(proxy)).Body);
    }

    [Fact]
    public async Task No_header_or_query_value_reaches_the_capture_file()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(capture: true);
        var body = Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"capture-body-marker"}]}""");
        string[] secrets = ["secret-authorization-7f3a", "secret-api-key-91bc", "secret-cookie-4d2e", "secret-query-key-c0de"];
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true, UseCookies = false };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(upstream.BaseAddress, $"/v1/messages?key={secrets[3]}"))
        {
            Content = Json(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets[0]);
        request.Headers.Add("x-api-key", secrets[1]);
        request.Headers.Add("Cookie", $"session={secrets[2]}");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var received = Assert.Single(upstream.Requests);
        Assert.Equal($"Bearer {secrets[0]}", received.Headers["Authorization"]);
        Assert.Single(await ReadCapturesAsync(proxy));
        var stored = ReadCaptureFiles(proxy);
        Assert.True(Contains(stored, "capture-body-marker"u8.ToArray()), "the scan must see the body, or it proves nothing");
        foreach (var secret in secrets)
        {
            Assert.False(Contains(stored, Encoding.UTF8.GetBytes(secret)), $"{secret} was found in the capture files");
        }
    }

    [Fact]
    public async Task Capture_off_stores_nothing_and_forwards_the_body_unchanged()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(capture: false);
        var body = Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"hello"}]}""");

        using var response = await SendAsync(proxy, upstream, body);

        Assert.Equal(body, Assert.Single(upstream.Requests).BodyBytes);
        Assert.Null(proxy.Services.GetService<IRequestCaptureStore>());
        Assert.False(proxy.Services.GetRequiredService<TitaniumProxyService>().Server.EnableRequestTimingCapture, "capture off must leave Titanium's timing off");
        Assert.Empty(Directory.GetFiles(proxy.DataDirectory, $"{SqliteCaptureStore.FileName}*"));
    }

    [Fact]
    public async Task A_failing_capture_store_leaves_the_request_alone_and_is_logged()
    {
        var logs = new RecordingLoggerProvider();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(capture: true, store: new ThrowingCaptureStore(), logs: logs);
        var body = Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"hello"}]}""");

        using var response = await SendAsync(proxy, upstream, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(body, Assert.Single(upstream.Requests).BodyBytes);
        var entry = Assert.Single(logs.Entries, entry => entry.EventId == CaptureFailedEventId);
        Assert.Equal(typeof(ProviderRequestInspector).FullName, entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<IOException>(entry.Exception);
    }

    [Fact]
    public async Task A_failing_capture_store_does_not_stop_compression()
    {
        var logs = new RecordingLoggerProvider();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(capture: true, compression: true, store: new ThrowingCaptureStore(), logs: logs);
        var body = ToolResultBody();

        using var response = await SendAsync(proxy, upstream, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(upstream.Requests).BodyBytes;
        Assert.True(forwarded.Length < body.Length, $"the upstream got {forwarded.Length} bytes, the client sent {body.Length}: the body was not compressed");
        var entry = Assert.Single(logs.Entries, entry => entry.EventId == CaptureFailedEventId);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public async Task Traffic_to_a_host_that_is_not_a_provider_is_never_captured()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(
            new Dictionary<string, string> { ["PROXY_CAPTURE"] = "on" },
            providerHosts: new SingleProviderHost("127.0.0.1", Dialect.Anthropic));
        var providerBody = Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"provider-marker"}]}""");
        var otherBody = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"tools/list","params":{"marker":"pass-through-marker"}}""");
        var otherHost = new UriBuilder(upstream.BaseAddress) { Host = "localhost" }.Uri;

        using var providerResponse = await SendAsync(proxy, upstream.BaseAddress, providerBody);
        using var otherResponse = await SendAsync(proxy, otherHost, otherBody);

        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
        Assert.Equal(new[] { providerBody, otherBody }, upstream.Requests.Select(request => request.BodyBytes).ToArray());
        var row = Assert.Single(await ReadCapturesAsync(proxy));
        Assert.Equal(providerBody, row.Body);
        Assert.False(Contains(ReadCaptureFiles(proxy), "pass-through-marker"u8.ToArray()), "the pass-through body was found in the capture files");
    }

    [Fact]
    public async Task Startup_says_once_that_capture_is_on_and_where_it_writes()
    {
        var logs = new RecordingLoggerProvider();
        await using var proxy = await StartProxyAsync(capture: true, logs: logs);

        var entry = Assert.Single(logs.Entries, entry => entry.EventId == 1501);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(Path.Combine(proxy.DataDirectory, SqliteCaptureStore.FileName), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_says_nothing_about_capture_when_it_is_off()
    {
        var logs = new RecordingLoggerProvider();
        await using var proxy = await StartProxyAsync(capture: false, logs: logs);

        Assert.DoesNotContain(logs.Entries, entry => entry.EventId == 1501);
    }

    [Fact]
    public async Task A_captured_request_gets_a_response_row_with_its_usage_sizes_and_timings_under_the_same_id()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 30, 15, TimeSpan.Zero);
        await using var upstream = await FakeUpstream.StartAsync();
        var encoded = Gzip(Encoding.UTF8.GetBytes(UsageMessage));
        upstream.Handler = (_, context) =>
        {
            context.Response.StatusCode = 201;
            context.Response.ContentType = "application/json";
            context.Response.Headers.ContentEncoding = "gzip";
            return context.Response.Body.WriteAsync(encoded).AsTask();
        };
        await using var proxy = await StartProxyAsync(capture: true, time: new FakeTimeProvider(now));
        var body = Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"hello"}]}""");

        using var response = await SendAsync(proxy, upstream, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = Assert.Single(await ReadResponsesAsync(proxy));
        Assert.Equal(Assert.Single(await ReadCapturesAsync(proxy)).RequestId, row.RequestId);
        Assert.Equal(now, DateTimeOffset.Parse(row.CompletedAt, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(201, row.Status);
        Assert.Equal(body.Length, row.RequestBytes);
        Assert.Equal(body.Length, row.ForwardedBytes);
        Assert.Equal(encoded.Length, row.ResponseBytes);
        Assert.Equal((11L, 22L, 33L, 44L), (row.InputTokens, row.OutputTokens, row.CacheCreationInputTokens, row.CacheReadInputTokens));
        Assert.Null(row.UsageError);
        var stages = Stages(row);
        foreach (var stage in new[] { "body-read", "capture-write", "inspector", "usage-read", "titanium.ttfb", "titanium.total" })
        {
            Assert.True(stages.ContainsKey(stage), $"no {stage} stage in {row.Stages}");
        }

        Assert.DoesNotContain("compression", stages.Keys);
    }

    [Fact]
    public async Task A_streamed_response_reaches_the_client_as_it_is_sent_and_its_usage_is_read_at_the_end()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (first, rest) = (Encoding.UTF8.GetBytes(StreamStart), Encoding.UTF8.GetBytes(StreamEnd));
        upstream.Handler = async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.WriteAsync(first);
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await context.Response.Body.WriteAsync(rest);
        };
        await using var proxy = await StartProxyAsync(capture: true);
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(upstream.BaseAddress, "/v1/messages")) { Content = Json("""{"model":"m","stream":true}"""u8.ToArray()) };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var head = new byte[first.Length];
        // The upstream holds the rest back until the client has the first event: a proxy that buffered the stream never gets here.
        await stream.ReadExactlyAsync(head, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.SetResult();
        using var tail = new MemoryStream();
        await stream.CopyToAsync(tail, TestContext.Current.CancellationToken);

        Assert.Equal(first, head);
        Assert.Equal(rest, tail.ToArray());
        var row = Assert.Single(await ReadResponsesAsync(proxy));
        Assert.Equal((100L, 57L, 200L, 301L), (row.InputTokens, row.OutputTokens, row.CacheCreationInputTokens, row.CacheReadInputTokens));
        Assert.Equal(first.Length + rest.Length, row.ResponseBytes);
    }

    [Fact]
    public async Task With_compression_on_the_response_row_has_the_compression_stages_and_the_forwarded_size()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static (_, context) =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(UsageMessage);
        };
        await using var proxy = await StartProxyAsync(capture: true, compression: true);
        var body = ToolResultBody();

        using var response = await SendAsync(proxy, upstream, body);

        var row = Assert.Single(await ReadResponsesAsync(proxy));
        Assert.Equal(body.Length, row.RequestBytes);
        Assert.Equal(Assert.Single(upstream.Requests).BodyBytes.Length, row.ForwardedBytes);
        Assert.True(row.ForwardedBytes < row.RequestBytes, "the body was not compressed");
        var stages = Stages(row);
        foreach (var stage in new[] { "compression", "compression.find", "compression.reduce.Log", "compression.store", "set-body" })
        {
            Assert.True(stages.ContainsKey(stage), $"no {stage} stage in {row.Stages}");
        }

        Assert.True(response.Headers.Contains("x-ai-proxy-content-id"), "the content-id header was lost");
        Assert.Contains("answer-marker", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(Contains(ReadCaptureFiles(proxy), "answer-marker"u8.ToArray()), "the response body was found in the capture files");
    }

    [Fact]
    public async Task A_response_over_the_cap_is_relayed_whole_and_its_usage_is_not_read()
    {
        var big = new byte[5 * 1024 * 1024 + 17];
        Array.Fill(big, (byte)'x');
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = (_, context) =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.Body.WriteAsync(big).AsTask();
        };
        await using var proxy = await StartProxyAsync(capture: true);

        using var response = await SendAsync(proxy, upstream, """{"model":"m"}"""u8.ToArray());

        Assert.Equal(big.Length, (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);
        var row = Assert.Single(await ReadResponsesAsync(proxy));
        Assert.Equal("over-cap", row.UsageError);
        Assert.Null(row.InputTokens);
        Assert.Equal(big.Length, row.ResponseBytes);
    }

    [Fact]
    public async Task A_content_encoding_the_reader_does_not_know_is_named_in_the_row()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static (_, context) =>
        {
            context.Response.ContentType = "application/json";
            context.Response.Headers.ContentEncoding = "zstd";
            return context.Response.WriteAsync("opaque");
        };
        await using var proxy = await StartProxyAsync(capture: true);

        using var response = await SendAsync(proxy, upstream, """{"model":"m"}"""u8.ToArray());

        Assert.Equal("opaque", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("encoding:zstd", Assert.Single(await ReadResponsesAsync(proxy)).UsageError);
    }

    [Fact]
    public async Task A_failing_response_write_is_logged_and_the_response_still_reaches_the_client()
    {
        var logs = new RecordingLoggerProvider();
        await using var upstream = await FakeUpstream.StartAsync();
        var store = new ResponseThrowingCaptureStore();
        await using var proxy = await StartProxyAsync(capture: true, store: store, logs: logs);

        using var response = await SendAsync(proxy, upstream, """{"model":"m"}"""u8.ToArray());

        Assert.Equal("upstream ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var entry = await WaitForAsync(() => logs.Entries.SingleOrDefault(entry => entry.EventId == ResponseCaptureFailedEventId));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<IOException>(entry.Exception);
        Assert.Equal(1, store.Requests);
    }

    [Fact]
    public async Task An_after_response_handler_added_once_the_proxy_runs_sees_the_response_row_already_written()
    {
        // The absence test below relies on this ordering: Titanium awaits AfterResponse handlers one after another.
        var store = new RecordingCaptureStore { ResponseDelay = TimeSpan.FromMilliseconds(300) };
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(capture: true, store: store);
        var seen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Services.GetRequiredService<TitaniumProxyService>().Server.AfterResponse += (_, _) =>
        {
            seen.TrySetResult(store.Responses.Count);
            return Task.CompletedTask;
        };

        using var response = await SendAsync(proxy, upstream, """{"model":"m"}"""u8.ToArray());

        Assert.Equal(1, await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_request_whose_capture_failed_gets_no_response_row()
    {
        var store = new ThrowingCaptureStore();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(capture: true, store: store);
        var finished = 0;
        proxy.Services.GetRequiredService<TitaniumProxyService>().Server.AfterResponse += (_, _) =>
        {
            Interlocked.Increment(ref finished);
            return Task.CompletedTask;
        };

        using var response = await SendAsync(proxy, upstream, """{"model":"m"}"""u8.ToArray());
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var second = await SendAsync(proxy, upstream, """{"model":"m"}"""u8.ToArray());
        await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // This handler runs after the proxy's own, so once it has seen both exchanges the proxy is done with them.
        await WaitForAsync(() => Volatile.Read(ref finished) >= 2 ? "done" : null);

        Assert.Equal(0, store.Responses);
    }

    [Theory]
    [InlineData(Dialect.OpenAi)]
    [InlineData(Dialect.Gemini)]
    public async Task Non_Anthropic_response_usage_is_not_recorded_as_Anthropic_tokens(Dialect dialect)
    {
        const string responseBody = """{"usage":{"input_tokens":100,"output_tokens":20,"input_tokens_details":{"cached_tokens":80}}}""";
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static (_, context) =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(responseBody);
        };
        await using var proxy = await ProxyHost.StartAsync(
            new Dictionary<string, string> { ["PROXY_CAPTURE"] = "on" },
            providerHosts: new LoopbackProviderHosts(dialect));

        using var response = await SendAsync(proxy, upstream, """{"model":"m"}"""u8.ToArray());

        Assert.Equal(responseBody, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(dialect.ToString(), Assert.Single(await ReadCapturesAsync(proxy)).Provider);
        var row = Assert.Single(await ReadResponsesAsync(proxy));
        Assert.Equal("no-usage", row.UsageError);
        Assert.Equal((null, null, null, null), (row.InputTokens, row.OutputTokens, row.CacheCreationInputTokens, row.CacheReadInputTokens));
        Assert.Equal(Encoding.UTF8.GetByteCount(responseBody), row.ResponseBytes);
    }

    [Fact]
    public async Task An_error_status_still_gets_a_row_with_the_status_and_no_usage()
    {
        const string error = """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""";
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static (_, context) =>
        {
            context.Response.StatusCode = 529;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(error);
        };
        await using var proxy = await StartProxyAsync(capture: true);

        using var response = await SendAsync(proxy, upstream, """{"model":"m"}"""u8.ToArray());

        Assert.Equal(529, (int)response.StatusCode);
        var row = Assert.Single(await ReadResponsesAsync(proxy));
        Assert.Equal(529, row.Status);
        Assert.Equal("no-usage", row.UsageError);
        Assert.Equal((null, null, null, null), (row.InputTokens, row.OutputTokens, row.CacheCreationInputTokens, row.CacheReadInputTokens));
        Assert.Equal(Encoding.UTF8.GetByteCount(error), row.ResponseBytes);
    }

    [Fact]
    public async Task A_plain_event_stream_far_over_the_cap_still_gives_its_whole_usage()
    {
        var events = new StringBuilder(StreamStart);
        while (events.Length < 5 * 1024 * 1024)
        {
            events.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"filler text\"}}\n\n");
        }

        var stream = Encoding.UTF8.GetBytes(events.Append(StreamEnd).ToString());
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            for (var offset = 0; offset < stream.Length; offset += 100_003)
            {
                await context.Response.Body.WriteAsync(stream.AsMemory(offset, Math.Min(100_003, stream.Length - offset)));
                await context.Response.Body.FlushAsync();
            }
        };
        await using var proxy = await StartProxyAsync(capture: true);

        using var response = await SendAsync(proxy, upstream, """{"model":"m","stream":true}"""u8.ToArray());

        Assert.Equal(stream, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        var row = Assert.Single(await ReadResponsesAsync(proxy));
        Assert.Null(row.UsageError);
        Assert.Equal((100L, 57L, 200L, 301L), (row.InputTokens, row.OutputTokens, row.CacheCreationInputTokens, row.CacheReadInputTokens));
        Assert.Equal(stream.Length, row.ResponseBytes);
    }

    [Fact]
    public async Task The_inspector_stages_hold_the_time_their_step_took()
    {
        var time = new FakeTimeProvider();
        var store = new RecordingCaptureStore { OnRequest = () => time.Advance(TimeSpan.FromMilliseconds(5)) };
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static (_, context) =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(UsageMessage);
        };
        await using var proxy = await StartProxyAsync(capture: true, store: store, time: time);

        using var response = await SendAsync(proxy, upstream, """{"model":"m"}"""u8.ToArray());

        var stages = (await WaitForAsync(() => store.Responses.FirstOrDefault())).Stages.ToDictionary(stage => stage.Name, stage => stage.Elapsed);
        Assert.Equal(TimeSpan.Zero, stages["body-read"]);
        Assert.Equal(TimeSpan.FromMilliseconds(5), stages["capture-write"]);
        Assert.Equal(TimeSpan.FromMilliseconds(5), stages["inspector"]);
        Assert.Equal(TimeSpan.Zero, stages["usage-read"]);
    }

    [Fact]
    public async Task Titanium_times_the_wait_for_the_first_byte_apart_from_the_whole_exchange()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static async (_, context) =>
        {
            await Task.Delay(300);
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(StreamStart));
            await context.Response.Body.FlushAsync();
            await Task.Delay(300);
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(StreamEnd));
        };
        await using var proxy = await StartProxyAsync(capture: true);

        using var response = await SendAsync(proxy, upstream, """{"model":"m","stream":true}"""u8.ToArray());

        var stages = Stages(Assert.Single(await ReadResponsesAsync(proxy)));
        var (firstByte, total) = (stages["titanium.ttfb"], stages["titanium.total"]);
        Assert.InRange(firstByte, 250_000, 10_000_000);
        Assert.True(total - firstByte >= 250_000, $"ttfb {firstByte} us and total {total} us: the second write's wait is not in the total");
        Assert.True(stages["inspector"] < firstByte, $"inspector {stages["inspector"]} us is not below ttfb {firstByte} us");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_capture_attaches_a_reader_to_the_response_stream(bool capture)
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await StartProxyAsync(capture: capture, compression: true);
        var field = typeof(Titanium.Web.Proxy.EventArguments.SessionEventArgsBase).GetField("DataReceived", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var attached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Services.GetRequiredService<TitaniumProxyService>().Server.BeforeResponse += (_, e) =>
        {
            attached.TrySetResult(field.GetValue(e) is not null);
            return Task.CompletedTask;
        };

        using var response = await SendAsync(proxy, upstream, ToolResultBody());

        Assert.Equal(capture, await attached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_upstream_that_drops_the_stream_midway_leaves_a_row_with_what_passed()
    {
        var head = Encoding.UTF8.GetBytes($"{StreamStart}event: message_delta\ndata: {{\"type\":\"message_delta\",\"usage\":{{\"output_to");
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.WriteAsync(head);
            await context.Response.Body.FlushAsync();
            await Task.Delay(100);
            context.Abort();
        };
        await using var proxy = await StartProxyAsync(capture: true);

        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.PostAsync(new Uri(upstream.BaseAddress, "/v1/messages"), Json("""{"model":"m","stream":true}"""u8.ToArray()), TestContext.Current.CancellationToken);
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Titanium leaves the client waiting until its own timeout, with capture off as well; the row is what is tested here.
        }

        var row = Assert.Single(await ReadResponsesAsync(proxy));
        Assert.Equal(head.Length, row.ResponseBytes);
        Assert.Equal((100L, 1L, 200L, 300L), (row.InputTokens, row.OutputTokens, row.CacheCreationInputTokens, row.CacheReadInputTokens));
        Assert.Equal("truncated", row.UsageError);
    }

    private const string UsageMessage = """
        {"id":"msg_1","type":"message","content":[{"type":"text","text":"answer-marker"}],
         "usage":{"input_tokens":11,"output_tokens":22,"cache_creation_input_tokens":33,"cache_read_input_tokens":44}}
        """;

    private const string StreamStart = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":100,"output_tokens":1,"cache_creation_input_tokens":200,"cache_read_input_tokens":300}}}


        """;

    private const string StreamEnd = """
        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":57,"cache_read_input_tokens":301}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    private const int ResponseCaptureFailedEventId = 1404;

    private static Dictionary<string, long> Stages(ResponseRow row) =>
        JsonSerializer.Deserialize<Dictionary<string, long>>(row.Stages)!;

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
    }

    /// <summary>The response row is written after the client has the response, so the test waits for it.</summary>
    private static async Task<IReadOnlyList<ResponseRow>> ReadResponsesAsync(ProxyHost proxy)
    {
        var path = Path.Combine(proxy.DataDirectory, SqliteCaptureStore.FileName);
        return await WaitForAsync(async () =>
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var rows = (await connection.QueryAsync<ResponseRow>("""
                SELECT request_id AS RequestId, completed_at AS CompletedAt, status AS Status,
                       request_bytes AS RequestBytes, forwarded_bytes AS ForwardedBytes, response_bytes AS ResponseBytes,
                       input_tokens AS InputTokens, output_tokens AS OutputTokens,
                       cache_creation_input_tokens AS CacheCreationInputTokens, cache_read_input_tokens AS CacheReadInputTokens,
                       usage_error AS UsageError, stages AS Stages
                FROM capture_response;
                """)).ToList();
            return rows.Count == 0 ? null : rows;
        });
    }

    private static Task<T> WaitForAsync<T>(Func<T?> probe)
        where T : class =>
        WaitForAsync(() => Task.FromResult(probe()));

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            T? value = null;
            try
            {
                value = await probe();
            }
            catch (SqliteException) when (!deadline.IsCancellationRequested)
            {
            }

            if (value is not null)
            {
                return value;
            }

            Assert.False(deadline.IsCancellationRequested, "timed out waiting for the capture");
            try
            {
                await Task.Delay(20, deadline.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Keeps what the proxy hands it in memory; can move a fake clock or stall, to time a step.</summary>
    private sealed class RecordingCaptureStore : IRequestCaptureStore
    {
        private readonly ConcurrentQueue<CapturedResponse> _responses = new();

        public Action? OnRequest { get; init; }

        public TimeSpan ResponseDelay { get; init; }

        public IReadOnlyCollection<CapturedResponse> Responses => _responses.ToArray();

        public ValueTask StoreAsync(CapturedRequest request, CancellationToken cancellationToken)
        {
            OnRequest?.Invoke();
            return ValueTask.CompletedTask;
        }

        public async ValueTask StoreResponseAsync(CapturedResponse response, CancellationToken cancellationToken)
        {
            await Task.Delay(ResponseDelay, cancellationToken);
            _responses.Enqueue(response);
        }
    }

    private sealed record ResponseRow(
        string RequestId,
        string CompletedAt,
        long Status,
        long RequestBytes,
        long ForwardedBytes,
        long ResponseBytes,
        long? InputTokens,
        long? OutputTokens,
        long? CacheCreationInputTokens,
        long? CacheReadInputTokens,
        string? UsageError,
        string Stages);

    private sealed class ResponseThrowingCaptureStore : IRequestCaptureStore
    {
        private int _requests;

        public int Requests => _requests;

        public ValueTask StoreAsync(CapturedRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return ValueTask.CompletedTask;
        }

        public ValueTask StoreResponseAsync(CapturedResponse response, CancellationToken cancellationToken) =>
            throw new IOException("disk full");
    }

    private Task<ProxyHost> StartProxyAsync(bool capture, bool compression = false, IRequestCaptureStore? store = null, ILoggerProvider? logs = null, TimeProvider? time = null)
    {
        var configuration = new Dictionary<string, string>();
        if (capture)
        {
            configuration["PROXY_CAPTURE"] = "on";
        }

        if (compression)
        {
            configuration["PROXY_COMPRESSION"] = "on";
            configuration["PROXY_COMPRESSION_DB"] = _contentDatabasePath;
        }

        return ProxyHost.StartAsync(configuration, providerHosts: new LoopbackProviderHosts(Dialect.Anthropic), capture: store, logging: logs, time: time);
    }

    private static byte[] ToolResultBody() =>
        Encoding.UTF8.GetBytes(
            $$"""{"model":"claude-haiku-4-5","max_tokens":5,"messages":[{"role":"user","content":[{"type":"tool_result","content":{{JsonSerializer.Serialize(ToolOutput)}}}]}]}""");

    private static ByteArrayContent Json(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static Task<HttpResponseMessage> SendAsync(ProxyHost proxy, FakeUpstream upstream, byte[] body) =>
        SendAsync(proxy, upstream.BaseAddress, body);

    private static async Task<HttpResponseMessage> SendAsync(ProxyHost proxy, Uri upstream, byte[] body)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        return await client.PostAsync(new Uri(upstream, "/v1/messages"), Json(body), TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<CaptureRow>> ReadCapturesAsync(ProxyHost proxy)
    {
        var path = Path.Combine(proxy.DataDirectory, SqliteCaptureStore.FileName);
        Assert.True(File.Exists(path), $"no capture file at {path}");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<CaptureRow>("""
            SELECT request_id AS RequestId, captured_at AS CapturedAt, provider AS Provider,
                   host AS Host, path AS Path, body AS Body
            FROM capture_request;
            """);
        return rows.ToList();
    }

    /// <summary>Every byte of the capture database and its journal files, wherever SQLite put a page.</summary>
    private static byte[] ReadCaptureFiles(ProxyHost proxy)
    {
        using var all = new MemoryStream();
        foreach (var file in Directory.GetFiles(proxy.DataDirectory, $"{SqliteCaptureStore.FileName}*"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.CopyTo(all);
        }

        return all.ToArray();
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private sealed record CaptureRow(string RequestId, string CapturedAt, string Provider, string Host, string Path, byte[] Body);

    /// <summary>Treats one host as a provider and every other host as pass-through traffic.</summary>
    private sealed class SingleProviderHost(string host, Dialect dialect) : IProviderHosts
    {
        public Dialect? Find(string candidate) => string.Equals(candidate, host, StringComparison.OrdinalIgnoreCase) ? dialect : null;
    }

    private sealed class ThrowingCaptureStore : IRequestCaptureStore
    {
        private int _responses;

        public int Responses => _responses;

        public ValueTask StoreAsync(CapturedRequest request, CancellationToken cancellationToken) =>
            throw new IOException("disk full");

        public ValueTask StoreResponseAsync(CapturedResponse response, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _responses);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, int EventId, string Message, Exception? Exception);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId.Id, formatter(state, exception), exception));
    }
}
