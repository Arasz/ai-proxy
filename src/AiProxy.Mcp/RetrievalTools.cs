using System.ComponentModel;
using System.Text.Json;
using AiProxy.Client;
using AiProxy.Core.Gain;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiProxy.Mcp;

/// <summary>Retrieves retained originals through the proxy's bounded retrieval API.</summary>
[McpServerToolType]
public sealed class RetrievalTools(ProxyClient client)
{
    [McpServerTool(Name = "retrieve_original", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Reads retained text the proxy shortened. Use its 16-hex preview ID, not a request GUID. Choose lines, pattern or whole explicitly.")]
    public async Task<CallToolResult> RetrieveOriginal(
        [Description("The 16-hex ID from a proxy preview.")] string id,
        [Description("Selection mode: lines, pattern or whole.")] string mode,
        int? start = null, int? end = null, string? pattern = null, int context = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.RetrieveAsync(new RetrievalRequest(id, mode, start, end, pattern, context), cancellationToken).ConfigureAwait(false);
            if (response.Text is null)
            {
                return Failed("The proxy retrieval API returned a response without text. Check the endpoint and proxy version.");
            }
            return new CallToolResult { IsError = response.Error is not null, Content = [new TextContentBlock { Text = response.Text }] };
        }
        catch (HttpRequestException exception)
        {
            var status = exception.StatusCode is { } code ? $" (HTTP {(int)code})" : "";
            return Failed($"The proxy retrieval API is unavailable{status}. Check that ai-proxy is running and --endpoint points to its communication URL.");
        }
        catch (JsonException)
        {
            return Failed("The proxy retrieval API returned invalid JSON. Check the endpoint and proxy version.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("The proxy retrieval API timed out. Check that ai-proxy is running and retry the retrieval.");
        }
    }
    private static CallToolResult Failed(string text) => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };
}
