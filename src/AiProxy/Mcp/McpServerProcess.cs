using AiProxy.Client;

namespace AiProxy.Mcp;

public sealed record McpServerOptions(string Endpoint);

/// <summary>Runs <c>ai-proxy mcp</c>: the retrieval MCP server on stdio, logging to stderr only.</summary>
public static class McpServerProcess
{
    /// <summary>The first argument that selects the MCP server.</summary>
    public const string Command = "mcp";

    /// <summary>Runs the stdio server against the configured proxy endpoint.</summary>
    public static async Task<int> RunAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("--endpoint must be an absolute HTTP or HTTPS proxy URL.", nameof(endpoint));
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(static console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddProxyHttpClient();
        builder.Services.AddSingleton(new McpServerOptions(endpoint));
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<RetrievalTools>();
        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}