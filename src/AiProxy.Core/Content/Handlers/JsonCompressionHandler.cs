using System.Text;
using System.Text.Json;

namespace AiProxy.Core.Content.Handlers;

/// <summary>Compacts a JSON document, keeping every value it holds.</summary>
public sealed class JsonCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports when it compacts the document.</summary>
    public const string CompactPath = "json.compact";

    /// <summary>The path this handler reports when the document does not parse.</summary>
    public const string FallbackPath = "json.dedupe";

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

            using (var writer = new Utf8JsonWriter(buffer))
            {
                document.RootElement.WriteTo(writer);
            }

            return ValueTask.FromResult(new HandledContent(Encoding.UTF8.GetString(buffer.ToArray()), CompactPath));
        }
        catch (JsonException)
        {
            var reduced = Reduction.DeduplicateLines(part.Text);
            var overlaid = MustKeepOverlay.Apply(reduced, part.Text);

            return ValueTask.FromResult(new HandledContent(overlaid.Text, FallbackPath));
        }
    }
}
