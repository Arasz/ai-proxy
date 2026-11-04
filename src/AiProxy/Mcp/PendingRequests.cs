using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace AiProxy.Mcp;

/// <summary>The JSON-RPC requests read from stdin that have not been answered yet.</summary>
internal sealed class PendingRequests
{
    private readonly HashSet<RequestId> _ids = [];
    private readonly Lock _gate = new();
    private TaskCompletionSource _empty = Completed();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _ids.Count;
            }
        }
    }

    /// <summary>The ids still waiting for a reply, in no particular order.</summary>
    public IReadOnlyCollection<RequestId> Ids
    {
        get
        {
            lock (_gate)
            {
                return [.. _ids];
            }
        }
    }

    /// <summary>A task that completes while no request is pending.</summary>
    public Task WhenEmpty
    {
        get
        {
            lock (_gate)
            {
                return _empty.Task;
            }
        }
    }

    /// <summary>Records the request on one input line, or forgets the request a cancel notification names; other lines change nothing.</summary>
    public void Track(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var method))
            {
                return;
            }

            if (root.TryGetProperty("id", out var id) && ToRequestId(id) is { } requestId)
            {
                Add(requestId);
            }
            else if (method.ValueKind == JsonValueKind.String && method.GetString() == "notifications/cancelled"
                && root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("requestId", out var target) && ToRequestId(target) is { } cancelled)
            {
                Complete(cancelled);
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>Runs <paramref name="send"/>, then releases the message's id when it is a response or error, even if the send throws.</summary>
    public async Task ReleaseAfterAsync(JsonRpcMessage message, Func<Task> send)
    {
        try
        {
            await send().ConfigureAwait(false);
        }
        finally
        {
            if (message is JsonRpcMessageWithId { Id: var id } and (JsonRpcResponse or JsonRpcError))
            {
                Complete(id);
            }
        }
    }

    public void Complete(RequestId id)
    {
        lock (_gate)
        {
            if (_ids.Remove(id) && _ids.Count == 0)
            {
                _empty.TrySetResult();
            }
        }
    }

    private void Add(RequestId id)
    {
        lock (_gate)
        {
            if (_ids.Add(id) && _ids.Count == 1)
            {
                _empty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private static RequestId? ToRequestId(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new RequestId(element.GetString()!),
        JsonValueKind.Number when element.TryGetInt64(out var number) => new RequestId(number),
        _ => null
    };

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }
}
