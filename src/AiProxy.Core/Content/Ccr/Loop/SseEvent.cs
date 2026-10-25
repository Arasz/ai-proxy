using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>One server-sent event: its name and its data line.</summary>
/// <param name="Name">The <c>event:</c> field.</param>
/// <param name="Data">The <c>data:</c> field, JSON for the Messages API.</param>
public sealed record SseEvent(string Name, string Data)
{
    /// <summary>The event as it goes on the wire.</summary>
    public byte[] ToBytes() => Encoding.UTF8.GetBytes($"event: {Name}\n{string.Concat(Data.Split('\n').Select(static line => $"data: {line}\n"))}\n");

    /// <summary>A keep-alive the client ignores.</summary>
    public static SseEvent Ping { get; } = Of(MessagesApi.Events.Ping, new JsonObject { [MessagesApi.Fields.Type] = MessagesApi.Events.Ping });

    /// <summary>An event whose data is the object.</summary>
    public static SseEvent Of(string name, JsonObject data) => new(name, data.ToJsonString(MessagesApi.Json));

    /// <summary>An <c>error</c> event the client treats like any upstream error.</summary>
    public static SseEvent Error(string type, string message) =>
        new(MessagesApi.Events.Error, JsonSerializer.Serialize(new ErrorBody(new ErrorDetail(type, message)), MessagesApi.Json));

    private sealed record ErrorBody(ErrorDetail Error)
    {
        public string Type => MessagesApi.Events.Error;
    }

    private sealed record ErrorDetail(string Type, string Message);
}
