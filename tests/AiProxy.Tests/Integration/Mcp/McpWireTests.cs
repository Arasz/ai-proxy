using System.Diagnostics;
using System.Text.Json;
using AiProxy.Core.Gain;
using AiProxy.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiProxy.Tests.Integration.Mcp;

public sealed class McpWireTests
{
    [Fact]
    public async Task Tools_list_declares_retrieve_original_and_all_four_boolean_hints()
    {
        await using var server = new McpProcess("http://127.0.0.1:1");
        await server.InitializeAsync();
        var response = await server.CallAsync("tools/list", new { });
        var tool = Assert.Single(response.GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal("retrieve_original", tool.GetProperty("name").GetString());
        Assert.Equal(McpToolInventoryGenerated.Names, response.GetProperty("result").GetProperty("tools").EnumerateArray().Select(static item => item.GetProperty("name").GetString()));
        var annotations = tool.GetProperty("annotations");
        Assert.Equal(JsonValueKind.True, annotations.GetProperty("readOnlyHint").ValueKind);
        Assert.Equal(JsonValueKind.False, annotations.GetProperty("destructiveHint").ValueKind);
        Assert.Equal(JsonValueKind.True, annotations.GetProperty("idempotentHint").ValueKind);
        Assert.Equal(JsonValueKind.False, annotations.GetProperty("openWorldHint").ValueKind);
        Assert.Contains("mode", tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(static item => item.GetString()));
    }

    [Theory]
    [InlineData("whole")]
    [InlineData("lines")]
    [InlineData("pattern")]
    public async Task Named_retrieve_original_call_forwards_selection_and_returns_exact_proxy_text(string mode)
    {
        const string original = "first 🙂\nsecond\nthird";
        await using var proxy = await ProxyStub.StartAsync(new RetrievalResponse(original, null));
        await using var server = new McpProcess(proxy.Endpoint);
        await server.InitializeAsync();
        object arguments = mode switch
        {
            "lines" => new { id = "0123456789abcdef", mode, start = 1, end = 2, context = 0 },
            "pattern" => new { id = "0123456789abcdef", mode, pattern = "second", context = 1 },
            _ => new { id = "0123456789abcdef", mode, context = 0 }
        };
        var response = await server.CallAsync("tools/call", new { name = "retrieve_original", arguments });
        var result = response.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Equal(original, Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        var request = Assert.Single(proxy.Requests);
        Assert.Equal("0123456789abcdef", request.GetProperty("id").GetString());
        Assert.Equal(mode, request.GetProperty("mode").GetString());
        if (mode == "lines")
        {
            Assert.Equal(1, request.GetProperty("start").GetInt32());
            Assert.Equal(2, request.GetProperty("end").GetInt32());
            Assert.False(request.TryGetProperty("pattern", out _));
        }
        else if (mode == "pattern")
        {
            Assert.Equal("second", request.GetProperty("pattern").GetString());
            Assert.Equal(1, request.GetProperty("context").GetInt32());
        }
        else
        {
            Assert.False(request.TryGetProperty("start", out _));
            Assert.False(request.TryGetProperty("end", out _));
        }
    }

    [Theory]
    [InlineData("not_kept")]
    [InlineData("bad_pattern")]
    [InlineData(null)]
    public async Task Named_retrieve_original_reports_domain_errors_but_zero_matches_are_success(string? error)
    {
        var text = error is null ? "[no line matches]" : $"[proxy_retrieve error {error}]";
        await using var proxy = await ProxyStub.StartAsync(new RetrievalResponse(text, error));
        await using var server = new McpProcess(proxy.Endpoint);
        await server.InitializeAsync();
        var response = await server.CallAsync("tools/call", new { name = "retrieve_original", arguments = new { id = "0123456789abcdef", mode = "pattern", pattern = "none" } });
        var result = response.GetProperty("result");
        var failed = result.TryGetProperty("isError", out var isError) && isError.GetBoolean();
        Assert.Equal(error is not null, failed);
        Assert.Equal(text, Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Named_retrieve_original_reports_proxy_HTTP_failure_as_actionable_tool_error()
    {
        await using var proxy = await ProxyStub.StartAsync(new RetrievalResponse("unavailable", null), 503);
        await using var server = new McpProcess(proxy.Endpoint);
        await server.InitializeAsync();
        var response = await server.CallAsync("tools/call", new { name = "retrieve_original", arguments = new { id = "0123456789abcdef", mode = "whole" } });
        var result = response.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString();
        Assert.Contains("proxy", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("503", text);
    }
}

internal sealed class ProxyStub(WebApplication app, string endpoint) : IAsyncDisposable
{
    public string Endpoint { get; } = endpoint;
    public List<JsonElement> Requests { get; } = [];

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    public static async Task<ProxyStub> StartAsync(RetrievalResponse response, int status = 200)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        ProxyStub? stub = null;
        app.MapPost("/retrieval", async context =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            stub!.Requests.Add(body.RootElement.Clone());
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(response);
        });
        await app.StartAsync();
        var endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        stub = new ProxyStub(app, endpoint);
        return stub;
    }
}

internal sealed class McpProcess(string endpoint) : IAsyncDisposable
{
    private readonly Launched _launched = Start(endpoint);
    private int _id;

    private Process Server => _launched.Process;

    private Task<string> Errors => _launched.Errors;

    public async ValueTask DisposeAsync()
    {
        Server.StandardInput.Close();
        if (!Server.HasExited) Server.Kill(true);
        await Server.WaitForExitAsync();
        Server.Dispose();
    }

    private static Launched Start(string endpoint)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AiProxy.dll"));
        start.ArgumentList.Add("mcp");
        start.ArgumentList.Add("--endpoint");
        start.ArgumentList.Add(endpoint);
        var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start the MCP server.");
        return new Launched(process, process.StandardError.ReadToEndAsync());
    }

    public async Task InitializeAsync()
    {
        await CallAsync("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "wire-test", version = "1" } });
        await Server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
        await Server.StandardInput.FlushAsync();
    }

    public async Task<JsonElement> CallAsync(string method, object parameters)
    {
        var id = ++_id;
        await Server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await Server.StandardInput.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var line = await Server.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.True(line is not null, $"MCP process closed stdout: {(Errors.IsCompleted ? await Errors : "stderr still open")}");
            using var document = JsonDocument.Parse(line!);
            if (document.RootElement.TryGetProperty("id", out var received) && received.ValueKind == JsonValueKind.Number && received.GetInt32() == id) return document.RootElement.Clone();
        }
    }

    /// <summary>The server process and its stderr, read from the start so the pipe never fills.</summary>
    private sealed record Launched(Process Process, Task<string> Errors);
}