using System.Text;

namespace AiProxy.Core.Content;

/// <summary>Puts the reduced parts back into the template they came from.</summary>
public sealed class ContentCompositor : ICompositor
{
    /// <inheritdoc />
    public string Compose(Decomposition decomposition, IReadOnlyList<CompressedPart> parts)
    {

        var reduced = parts.ToDictionary(part => part.ChunkId, part => part.Text);
        var placeholders = decomposition.Parts
            .Select(part => new
            {
                Placeholder = part.Type.Placeholder(part.ChunkId),
                Text = reduced.TryGetValue(part.ChunkId, out var text) ? text : part.Text
            })
            .ToList();

        var template = decomposition.Template;
        var composed = new StringBuilder(template.Length);
        var position = 0;

        foreach (var part in placeholders)
        {
            var index = template.IndexOf(part.Placeholder, position, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            composed.Append(template, position, index - position);
            composed.Append(part.Text);
            position = index + part.Placeholder.Length;
        }

        composed.Append(template, position, template.Length - position);

        return composed.ToString();
    }
}