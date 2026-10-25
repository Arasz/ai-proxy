using System.Text;
using System.Text.Json.Nodes;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>Builds the request that sends the proxy's tool results upstream after a retrieving response.</summary>
public static class RetrievalFollowUp
{
    /// <summary>
    /// A thread follow-up: the forwarded request with its messages replaced by the tool results and the
    /// thread continued from the retrieving response. A <c>create</c> becomes a <c>continue</c> and drops its tools.
    /// </summary>
    public static byte[] Thread(ReadOnlySpan<byte> forwarded, string previousMessageId, JsonArray toolResults)
    {
        var request = JsonNode.Parse(forwarded)!.AsObject();
        request["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = toolResults });
        request.Remove("tools");
        request.Remove("tool_choice");
        request["thread"] = new JsonObject { ["type"] = "continue", ["previous_message_id"] = previousMessageId };
        if (request["diagnostics"] is JsonObject diagnostics && diagnostics.ContainsKey("previous_message_id"))
        {
            diagnostics["previous_message_id"] = previousMessageId;
        }

        return Encoding.UTF8.GetBytes(request.ToJsonString(RetrievalJsonOptions.Relaxed));
    }

    /// <summary>A stateless follow-up: the forwarded request plus the assistant message as received and the tool results.</summary>
    public static byte[] Stateless(ReadOnlySpan<byte> forwarded, JsonArray assistantContent, JsonArray toolResults)
    {
        var request = JsonNode.Parse(forwarded)!.AsObject();
        var messages = request["messages"]!.AsArray();
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantContent });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
        return Encoding.UTF8.GetBytes(request.ToJsonString(RetrievalJsonOptions.Relaxed));
    }
}
