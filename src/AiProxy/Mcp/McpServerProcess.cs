using AiProxy.Client;

namespace AiProxy.Mcp;

/// <summary>Where the server sends retrieval calls, and the data directory it reads the proxy's API token from at each call.</summary>
/// <param name="Endpoint">The proxy API base URL.</param>
/// <param name="DataDirectory">The proxy's data directory.</param>
public sealed record McpServerOptions(string Endpoint, string DataDirectory);

/// <summary>Runs <c>ai-proxy mcp</c>: the retrieval MCP server on stdio, logging to stderr only.</summary>
public static class McpServerProcess
{
    private static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(30);

    /// <summary>The first argument that selects the MCP server.</summary>
    public const string Command = "mcp";

    /// <summary>True when <c>GET {endpoint}/health</c> answers success within two seconds; never throws for an unreachable proxy.</summary>
    public static async Task<bool> IsReachableAsync(string endpoint, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
    {
        using var http = httpClientFactory.CreateClient(ProxyClientServiceCollectionExtensions.ProxyClientName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await http.GetAsync(new Uri(new Uri(endpoint), "health"), deadline.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Runs the stdio server against the configured proxy endpoint; at end of input it still answers requests in flight.</summary>
    public static async Task<int> RunAsync(string endpoint, string dataDirectory, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("--endpoint must be an absolute HTTP or HTTPS proxy URL.");
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(static console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddProxyHttpClient();
        builder.Services.AddSingleton(new McpServerOptions(endpoint, dataDirectory));
        using var drainLog = LoggerFactory.Create(static log => log.AddConsole(static console => console.LogToStandardErrorThreshold = LogLevel.Trace));
        var pending = new PendingRequests();
        var input = new DrainOnEofStream(Console.OpenStandardInput(), pending, TimeProvider.System, DrainBound, drainLog.CreateLogger<DrainOnEofStream>());
        builder.Services.AddMcpServer(options => options.Filters.Message.OutgoingFilters.Add(next => async (context, token) =>
            {
                await pending.ReleaseAfterAsync(context.JsonRpcMessage, () => next(context, token)).ConfigureAwait(false);
            }))
            .WithStreamServerTransport(input, new BufferedStream(Console.OpenStandardOutput()))
            .WithTools([RetrievalTools.CreateTool()]);
        using var host = builder.Build();
        if (!await IsReachableAsync(endpoint, host.Services.GetRequiredService<IHttpClientFactory>(), cancellationToken).ConfigureAwait(false))
        {
            await Console.Error.WriteLineAsync($"ai-proxy: warning: no proxy answers at {endpoint}; retrieval calls fail until it starts.", cancellationToken).ConfigureAwait(false);
        }

        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
