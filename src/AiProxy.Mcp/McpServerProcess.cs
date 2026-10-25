using AiProxy.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiProxy.Mcp;

/// <summary>Runs <c>ai-proxy mcp</c>: the retrieval MCP server on stdio, logging to stderr only.</summary>
public sealed class McpServerProcess
{
    /// <summary>The first argument that selects the MCP server.</summary>
    public const string Command = "mcp";

    private const string Usage = "Usage: ai-proxy mcp [--endpoint http://127.0.0.1:17432]";

    private const string EndpointOption = "--endpoint";

    /// <summary>Runs the server with the arguments after <c>mcp</c> and returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] options)
    {
        if (options.Length is not 0 && (options.Length != 2 || options[0] != EndpointOption))
        {
            await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        var endpoint = options.Length == 2 ? options[1] : ProxyClientOptions.DefaultEndpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            await Console.Error.WriteLineAsync("--endpoint must be an absolute HTTP or HTTPS proxy URL.").ConfigureAwait(false);
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        using var http = ProxyClient.CreateHttpClient(endpoint);
        builder.Services.AddSingleton(new ProxyClient(http));
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<RetrievalTools>();
        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
