using System.Text.Json.Nodes;
using static AiProxy.Core.Content.Ccr.MessagesApi;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>Reads and edits of Messages API JSON that tolerate a missing or mistyped value.</summary>
public static class MessagesJsonExtensions
{
    extension(JsonObject node)
    {
        /// <summary>The string under the name, or empty when it is missing or not a string.</summary>
        public string TextOf(string name)
        {
            return node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        }

        /// <summary>The whole number under the name, or null when it is missing or not one.</summary>
        public int? NumberOf(string name)
        {
            return node[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
        }

        /// <summary>Appends a streamed string fragment to the string under the name.</summary>
        public void Append(string name, JsonNode? addition)
        {
            node[name] = node.TextOf(name) + (addition is JsonValue value && value.TryGetValue<string>(out var text) ? text : "");
        }
    }

    extension(SseEvent sse)
    {
        /// <summary>The event with its block <c>index</c> set to the client's; unchanged when the numbers agree or the data is not an object.</summary>
        public SseEvent Renumbered(int upstreamIndex, int clientIndex)
        {
            if (upstreamIndex == clientIndex || JsonNode.Parse(sse.Data) is not JsonObject data)
            {
                return sse;
            }

            data[Fields.Index] = clientIndex;
            return sse with { Data = data.ToJsonString(MessagesApi.Json) };
        }
    }
}