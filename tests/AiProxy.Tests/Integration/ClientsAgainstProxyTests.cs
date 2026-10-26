using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Providers;
using Microsoft.AspNetCore.Http;
using static AiProxy.Tests.Unit.Content.Ccr.RetrievalLoopTests;

namespace AiProxy.Tests.Integration;

/// <summary>Both clients of the one ai-proxy tool, run as their own processes against one running proxy: gain sees the reduction, mcp reads the kept original back.</summary>
public sealed class ClientsAgainstProxyTests : IDisposable
{
    private static readonly string Thread = string.Join("\n", Enumerable.Range(1, 600).Select(index => $"thread {index:D4}: compiled module m{index} into obj/m{index}.o"));

    private static readonly string Stateless = string.Join("\n", Enumerable.Range(1, 600).Select(index => $"stateless {index:D4}: linked module m{index} into bin/m{index}"));

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ai-proxy-clients-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Gain_reports_the_reduction_and_mcp_returns_the_thread_original_but_not_a_stateless_one()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(Sse.Message("msg_1", "end_turn", 1, Sse.Text("done")));
        };
        await using var proxy = await ProxyHost.StartAsync(
            new Dictionary<string, string>
            {
                ["Proxy:Compression:Enabled"] = "true",
                ["Proxy:Compression:Database"] = _databasePath,
            },
            providerHosts: new LoopbackProviderHosts(Dialect.Anthropic));

        await SendAsync(proxy, upstream, Request(Thread, thread: true));
        await SendAsync(proxy, upstream, Request(Stateless, thread: false));
        var endpoint = proxy.BaseAddress.ToString().TrimEnd('/');

        var gain = await RunAsync(null, "gain", "--endpoint", endpoint, "--format", "json");
        Assert.True(gain.ExitCode == 0, gain.Error);
        using var report = JsonDocument.Parse(gain.Output);
        var summary = Property(report.RootElement, "summary");
        Assert.Equal(2, Property(summary, "requests").GetInt64());
        Assert.True(Property(summary, "estimatedSavedTokens").GetInt64() > 0, gain.Output);

        var mcp = await RunAsync(
            Rpc(1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "clients-test", version = "1" } })
            + Notification("notifications/initialized")
            + Rpc(2, "tools/call", new { name = "retrieve_original", arguments = new { id = CcrView.IdOf(Thread), mode = "whole" } })
            + Rpc(3, "tools/call", new { name = "retrieve_original", arguments = new { id = CcrView.IdOf(Stateless), mode = "whole" } }),
            "mcp", "--endpoint", endpoint);
        var answers = mcp.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => JsonNode.Parse(line)!).ToDictionary(static node => node["id"]!.GetValue<int>());
        var kept = answers[2]["result"]!;
        Assert.NotEqual(true, kept["isError"]?.GetValue<bool>());
        Assert.EndsWith(Thread, kept["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var notKept = answers[3]["result"]!;
        Assert.True(notKept["isError"]!.GetValue<bool>());
        Assert.Contains(CcrRetrieval.NotKept, notKept["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static JsonElement Property(JsonElement element, string camelCase) =>
        element.TryGetProperty(camelCase, out var value)
            ? value
            : element.EnumerateObject().First(property => string.Equals(property.Name.Replace("_", "", StringComparison.Ordinal), camelCase, StringComparison.OrdinalIgnoreCase)).Value;

    private static string Request(string toolResult, bool thread)
    {
        var request = new JsonObject
        {
            ["model"] = "m",
            ["max_tokens"] = 5,
            ["stream"] = true,
            ["tools"] = new JsonArray(new JsonObject { ["name"] = "Read", ["input_schema"] = new JsonObject { ["type"] = "object" } }),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t0", ["content"] = toolResult }),
            }),
        };
        if (thread)
        {
            request["thread"] = new JsonObject { ["type"] = "create" };
        }

        return request.ToJsonString();
    }

    private static async Task SendAsync(ProxyHost proxy, FakeUpstream upstream, string body)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(upstream.BaseAddress, "/v1/messages"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        await response.Content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string Rpc(int id, string method, object parameters) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }) + "\n";

    private static string Notification(string method) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method }) + "\n";

    private static async Task<ProcessResult> RunAsync(string? input, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AiProxy.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = input is null ? process.StandardOutput.ReadToEndAsync(timeout.Token) : ReadAnswersAsync(process, input.Count(static c => c == '\n') - 1, timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        if (input is null)
        {
            process.StandardInput.Close();
        }
        else
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
        }

        var text = await output;
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, text, await error);
    }

    /// <summary>Reads one JSON-RPC answer per request; the server keeps running until its input closes.</summary>
    private static async Task<string> ReadAnswersAsync(Process process, int answers, CancellationToken cancellationToken)
    {
        var lines = new StringBuilder();
        for (var read = 0; read < answers;)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException("The MCP server closed its output early.");
            lines.Append(line).Append('\n');
            read++;
        }

        return lines.ToString();
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
