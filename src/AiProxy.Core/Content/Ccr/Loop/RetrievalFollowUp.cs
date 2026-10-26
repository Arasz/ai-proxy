using System.Text.Json;
using System.Text.Json.Nodes;
using static AiProxy.Core.Content.Ccr.MessagesApi;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>Builds the request that sends the proxy's tool results upstream after a retrieving response.</summary>
public static class RetrievalFollowUp
{
    /// <summary>
    ///     A thread follow-up: the forwarded request with its messages replaced by the tool results and the
    ///     thread continued from the retrieving response. A <c>create</c> becomes a <c>continue</c> and drops its tools.
    /// </summary>
    public static byte[] Thread(ReadOnlySpan<byte> forwarded, string previousMessageId, IReadOnlyList<ToolResult> toolResults)
    {
        var request = RequestOf(forwarded);
        request[Fields.Messages] = JsonSerializer.SerializeToNode(new[] { new Turn<IReadOnlyList<ToolResult>>(Roles.User, toolResults) }, MessagesApi.Json);
        request.Remove(Fields.Tools);
        request.Remove(Fields.ToolChoice);
        request[Fields.Thread] = new JsonObject { [Fields.Type] = ThreadTypes.Continue, [Fields.PreviousMessageId] = previousMessageId };
        if (request[Fields.Diagnostics] is JsonObject diagnostics && diagnostics.ContainsKey(Fields.PreviousMessageId)) diagnostics[Fields.PreviousMessageId] = previousMessageId;

        return JsonSerializer.SerializeToUtf8Bytes(request, MessagesApi.Json);
    }

    /// <summary>A stateless follow-up: the forwarded request plus the assistant message as received and the tool results.</summary>
    public static byte[] Stateless(ReadOnlySpan<byte> forwarded, JsonArray assistantContent, IReadOnlyList<ToolResult> toolResults)
    {
        var request = RequestOf(forwarded);
        if (request[Fields.Messages] is not JsonArray messages) throw new InvalidOperationException("The forwarded request has no messages array to continue.");

        messages.Add(JsonSerializer.SerializeToNode(new Turn<JsonArray>(Roles.Assistant, assistantContent), MessagesApi.Json));
        messages.Add(JsonSerializer.SerializeToNode(new Turn<IReadOnlyList<ToolResult>>(Roles.User, toolResults), MessagesApi.Json));
        return JsonSerializer.SerializeToUtf8Bytes(request, MessagesApi.Json);
    }

    private static JsonObject RequestOf(ReadOnlySpan<byte> forwarded)
    {
        return JsonNode.Parse(forwarded) as JsonObject
               ?? throw new InvalidOperationException("The forwarded request is not a JSON object.");
    }
}