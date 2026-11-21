using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>One server-sent event: its name and its data line.</summary>
/// <param name="Name">The <c>event:</c> field.</param>
/// <param name="Data">The <c>data:</c> field, JSON for the Messages API.</param>
public sealed record SseEvent(string Name, string Data)
{
    /// <summary>A keep-alive the client ignores.</summary>
    public static SseEvent Ping { get; } = Of(MessagesApi.Events.Ping, new JsonObject { [MessagesApi.Fields.Type] = MessagesApi.Events.Ping });

    /// <summary>The name of an event written without an <c>event:</c> line, as Chat Completions chunks are.</summary>
    public const string Unnamed = "";

    /// <summary>The name of a comment line, which clients ignore.</summary>
    public const string Comment = ":";

    /// <summary>A keep-alive comment line.</summary>
    public static SseEvent CommentLine { get; } = new(Comment, "keep-alive");

    /// <summary>The event as it goes on the wire.</summary>
    public byte[] ToBytes()
    {
        if (Name == Comment)
        {
            return Encoding.UTF8.GetBytes($": {Data}\n\n");
        }

        var nameLine = Name.Length == 0 ? "" : $"event: {Name}\n";
        return Encoding.UTF8.GetBytes($"{nameLine}{string.Concat(Data.Split('\n').Select(static line => $"data: {line}\n"))}\n");
    }

    /// <summary>An event whose data is the object.</summary>
    public static SseEvent Of(string name, JsonObject data)
    {
        return new SseEvent(name, data.ToJsonString(MessagesApi.Json));
    }

    /// <summary>An <c>error</c> event the client treats like any upstream error.</summary>
    public static SseEvent Error(string type, string message)
    {
        return new SseEvent(MessagesApi.Events.Error, JsonSerializer.Serialize(new ErrorBody(new ErrorDetail(type, message)), MessagesApi.Json));
    }

    private sealed record ErrorBody(ErrorDetail Error)
    {
        public string Type => MessagesApi.Events.Error;
    }

    private sealed record ErrorDetail(string Type, string Message);
}