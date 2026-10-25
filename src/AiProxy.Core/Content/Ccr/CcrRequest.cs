using System.Text;
using System.Text.Json;
using AiProxy.Core.Json;
using static AiProxy.Core.Content.Ccr.MessagesApi;

namespace AiProxy.Core.Content.Ccr;

/// <summary>Which conversation shape a Messages request uses.</summary>
public enum CcrThread
{
    /// <summary>No thread: the client re-sends the whole conversation.</summary>
    None,

    /// <summary>The first request of a server-side thread; it carries the tools.</summary>
    Create,

    /// <summary>A later request of a server-side thread; it carries only the new blocks.</summary>
    Continue,
}

/// <summary>How the client asked for the response.</summary>
public enum CcrResponse
{
    /// <summary>One JSON body.</summary>
    Whole,

    /// <summary>Server-sent events.</summary>
    Streamed,
}

/// <summary>Whether the request defines tools.</summary>
public enum CcrTools
{
    /// <summary>No tools, or an empty list.</summary>
    None,

    /// <summary>At least one tool.</summary>
    Defined,
}

/// <summary>What CCR reads from a Messages request body, and the byte-exact edits it makes to one.</summary>
/// <param name="Response">How the client asked for the response.</param>
/// <param name="Tools">Whether the request defines tools.</param>
/// <param name="Thread">The thread shape.</param>
/// <param name="PreviousMessageId">The message a <c>continue</c> follows; null otherwise.</param>
public sealed record CcrRequest(CcrResponse Response, CcrTools Tools, CcrThread Thread, string? PreviousMessageId)
{
    private static readonly byte[] DefinitionBytes = Encoding.UTF8.GetBytes(CcrRetrieval.Definition);

    /// <summary>Reads the request, or returns null when the body is not a JSON object.</summary>
    public static CcrRequest? Read(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var response = root.TryGetProperty(Fields.Stream, out var stream) && stream.ValueKind == JsonValueKind.True ? CcrResponse.Streamed : CcrResponse.Whole;
        var tools = root.TryGetProperty(Fields.Tools, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 ? CcrTools.Defined : CcrTools.None;
        var thread = CcrThread.None;
        string? previous = null;
        if (root.TryGetProperty(Fields.Thread, out var threadValue) && threadValue.ValueKind == JsonValueKind.Object)
        {
            thread = threadValue.StringProperty(Fields.Type) switch
            {
                ThreadTypes.Create => CcrThread.Create,
                ThreadTypes.Continue => CcrThread.Continue,
                _ => CcrThread.None,
            };
            previous = threadValue.StringProperty(Fields.PreviousMessageId);
        }

        return new CcrRequest(response, tools, thread, previous);
    }

    /// <summary>Appends the <c>proxy_retrieve</c> definition as the last tool; null when there are no tools or it is already there.</summary>
    public static byte[]? AppendTool(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body);
        var insertAt = -1;
        var count = 0;
        var inTools = false;
        var nameIsNext = false;
        while (reader.Read())
        {
            if (!inTools)
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals(Fields.Tools))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        return null;
                    }

                    inTools = true;
                }

                continue;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray when reader.CurrentDepth == 1:
                    insertAt = (int)reader.TokenStartIndex;
                    inTools = false;
                    break;
                case JsonTokenType.StartObject when reader.CurrentDepth == 2:
                    count++;
                    break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 3:
                    nameIsNext = reader.ValueTextEquals(Fields.Name);
                    break;
                case JsonTokenType.String when nameIsNext && reader.ValueTextEquals(CcrRetrieval.Name):
                    return null;
                default:
                    nameIsNext = false;
                    break;
            }

            if (insertAt >= 0)
            {
                break;
            }
        }

        if (insertAt < 0 || count == 0)
        {
            return null;
        }

        var result = new byte[body.Length + 1 + DefinitionBytes.Length];
        body[..insertAt].CopyTo(result);
        result[insertAt] = (byte)',';
        DefinitionBytes.CopyTo(result.AsSpan(insertAt + 1));
        body[insertAt..].CopyTo(result.AsSpan(insertAt + 1 + DefinitionBytes.Length));
        return result;
    }

    /// <summary>Points a <c>continue</c> at another message: <c>thread.previous_message_id</c> and <c>diagnostics.previous_message_id</c>.</summary>
    public static byte[] ContinueFrom(ReadOnlySpan<byte> body, string messageId)
    {
        var replacement = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(messageId));
        var spans = new List<TokenSpan>();
        var reader = new Utf8JsonReader(body);
        var parent = "";
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.CurrentDepth == 1)
            {
                parent = reader.GetString() ?? "";
                continue;
            }

            if (reader.CurrentDepth == 2 && parent is Fields.Thread or Fields.Diagnostics && reader.ValueTextEquals(Fields.PreviousMessageId))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                {
                    spans.Add(new TokenSpan((int)reader.TokenStartIndex, reader.ValueSpan.Length + 2));
                }
            }
        }

        using var buffer = new MemoryStream(body.Length + replacement.Length * spans.Count);
        var position = 0;
        foreach (var span in spans)
        {
            buffer.Write(body[position..span.Start]);
            buffer.Write(replacement);
            position = span.Start + span.Length;
        }

        buffer.Write(body[position..]);
        return buffer.ToArray();
    }

    /// <summary>Where a JSON string token sits in the body.</summary>
    private sealed record TokenSpan(int Start, int Length);
}
