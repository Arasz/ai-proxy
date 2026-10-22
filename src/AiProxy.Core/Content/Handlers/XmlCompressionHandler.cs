using System.Xml;
using System.Xml.Linq;

namespace AiProxy.Core.Content.Handlers;

/// <summary>Compacts an XML document by dropping the whitespace that only indents it.</summary>
public sealed class XmlCompressionHandler : ICompressionHandler
{
    /// <summary>The path this handler reports when it compacts the document.</summary>
    public const string CompactPath = "xml.compact";

    /// <summary>The path this handler reports when the document does not parse.</summary>
    public const string FallbackPath = "xml.dedupe";

    /// <inheritdoc />
    public bool CanHandle(ContentType type) => type == ContentType.Xml;

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        try
        {
            var document = XDocument.Parse(part.Text, LoadOptions.PreserveWhitespace);

            foreach (var node in document.DescendantNodes().OfType<XText>().Where(text => string.IsNullOrWhiteSpace(text.Value)).ToList())
            {
                node.Remove();
            }

            return ValueTask.FromResult(new HandledContent(document.ToString(SaveOptions.DisableFormatting), CompactPath));
        }
        catch (XmlException)
        {
            var reduced = Reduction.DeduplicateLines(part.Text);
            var overlaid = MustKeepOverlay.Apply(reduced, part.Text);

            return ValueTask.FromResult(new HandledContent(overlaid.Text, FallbackPath));
        }
    }
}
