using System.Text;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>One server-sent event: its name and its data line.</summary>
/// <param name="Name">The <c>event:</c> field.</param>
/// <param name="Data">The <c>data:</c> field, JSON for the Messages API.</param>
public sealed record SseEvent(string Name, string Data)
{
    /// <summary>The event as it goes on the wire.</summary>
    public byte[] ToBytes() => Encoding.UTF8.GetBytes($"event: {Name}\n{string.Concat(Data.Split('\n').Select(static line => $"data: {line}\n"))}\n");

    /// <summary>A keep-alive the client ignores.</summary>
    public static SseEvent Ping { get; } = new("ping", """{"type": "ping"}""");

    /// <summary>An <c>error</c> event the client treats like any upstream error.</summary>
    public static SseEvent Error(string type, string message) =>
        new("error", System.Text.Json.JsonSerializer.Serialize(new { type = "error", error = new { type, message } }));
}
