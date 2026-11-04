using AiProxy.Client;
using ModelContextProtocol.Protocol;

namespace AiProxy.Mcp;

public sealed record McpServerOptions(string Endpoint);

/// <summary>Runs <c>ai-proxy mcp</c>: the retrieval MCP server on stdio, logging to stderr only.</summary>
public static class McpServerProcess
{
    private static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(30);

    /// <summary>The first argument that selects the MCP server.</summary>
    public const string Command = "mcp";

    /// <summary>True when <c>GET {endpoint}/health</c> answers success within two seconds; never throws for an unreachable proxy.</summary>
    public static async Task<bool> IsReachableAsync(string endpoint, HttpMessageHandler handler, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler, false);
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
    public static async Task<int> RunAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("--endpoint must be an absolute HTTP or HTTPS proxy URL.");
        }

        using var probe = new SocketsHttpHandler { UseProxy = false };
        if (!await IsReachableAsync(endpoint, probe, cancellationToken).ConfigureAwait(false))
        {
            await Console.Error.WriteLineAsync($"ai-proxy: warning: no proxy answers at {endpoint}; retrieval calls fail until it starts.").ConfigureAwait(false);
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(static console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddProxyHttpClient();
        builder.Services.AddSingleton(new McpServerOptions(endpoint));
        var pending = new PendingRequests();
        var input = new DrainOnEofStream(Console.OpenStandardInput(), pending, TimeProvider.System, DrainBound);
        builder.Services.AddMcpServer(options => options.Filters.Message.OutgoingFilters.Add(next => async (context, token) =>
            {
                await next(context, token).ConfigureAwait(false);
                if (context.JsonRpcMessage is JsonRpcMessageWithId { Id: var id } and (JsonRpcResponse or JsonRpcError))
                {
                    pending.Complete(id);
                }
            }))
            .WithStreamServerTransport(input, new BufferedStream(Console.OpenStandardOutput()))
            .WithTools<RetrievalTools>();
        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}