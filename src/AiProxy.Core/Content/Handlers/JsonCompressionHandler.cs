using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiProxy.Core.Content.Handlers;

/// <summary>Compacts a JSON document, keeping every value it holds.</summary>
public sealed class JsonCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports when it compacts the document.</summary>
    public const string CompactPath = "json.compact";

    /// <summary>The path this handler reports when the document does not parse.</summary>
    public const string FallbackPath = "json.dedupe";

    /// <summary>Writes characters as they are: the text goes to a model, not into HTML, and an escape is longer.</summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <inheritdoc />
    public bool CanHandle(ContentType type) => type == ContentType.Json;

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        try
        {
            using var document = JsonDocument.Parse(part.Text);
            using var buffer = new MemoryStream();

            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                document.RootElement.WriteTo(writer);
            }

            var compact = Encoding.UTF8.GetString(buffer.ToArray());

            // The writer still escapes characters outside the basic plane, so a compact document can come out longer.
            return ValueTask.FromResult(new HandledContent(compact.Length <= part.Text.Length ? compact : part.Text, CompactPath));
        }
        catch (JsonException)
        {
            var reduced = Reduction.DeduplicateLines(part.Text);
            var overlaid = MustKeepOverlay.Apply(reduced, part.Text);

            return ValueTask.FromResult(new HandledContent(overlaid.Text, FallbackPath));
        }
    }
}
