using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiProxy.Core.Content.Ccr.Loop;

internal static class RetrievalJsonOptions
{
    /// <summary>Writes JSON as the API wrote it: no escaping of characters JSON does not require escaping.</summary>
    public static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
