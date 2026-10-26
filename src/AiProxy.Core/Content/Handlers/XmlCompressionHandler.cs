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

    private static readonly XNamespace XmlNamespace = XNamespace.Xml;

    /// <inheritdoc />
    public bool CanHandle(ContentType type)
    {
        return type == ContentType.Xml;
    }

    /// <inheritdoc />
    public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        try
        {
            var document = XDocument.Parse(part.Text, LoadOptions.PreserveWhitespace);

            foreach (var node in document.DescendantNodes().OfType<XText>().Where(IsIndentation).ToList())
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

    /// <summary>Whether a text node is the indentation rather than the content.</summary>
    /// <remarks>
    ///     Whitespace between elements is the formatting of a document, and dropping it is what
    ///     the reducer is for. Whitespace next to text is the content: "Hello
    ///     <b>world</b>" reads differently without the space, and <c>xml:space="preserve"</c>
    ///     says so outright.
    /// </remarks>
    /// <param name="text">The text node to judge.</param>
    private static bool IsIndentation(XText text)
    {
        if (!string.IsNullOrWhiteSpace(text.Value))
        {
            return false;
        }

        var parent = text.Parent;
        if (parent is null)
        {
            return false;
        }

        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Attribute(XmlNamespace + "space")?.Value == "preserve")
            {
                return false;
            }
        }

        var mixed = parent.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value));

        return !mixed;
    }
}