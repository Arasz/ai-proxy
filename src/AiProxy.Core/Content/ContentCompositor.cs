using System.Text;

namespace AiProxy.Core.Content;

/// <summary>Puts the reduced parts back into the template they came from.</summary>
public sealed class ContentCompositor : ICompositor
{
    /// <inheritdoc />
    public string Compose(Decomposition decomposition, IReadOnlyList<CompressedPart> parts)
    {
        ArgumentNullException.ThrowIfNull(decomposition);
        ArgumentNullException.ThrowIfNull(parts);

        var reduced = parts.ToDictionary(part => part.ChunkId, part => part.Text);
        var placeholders = decomposition.Parts
            .Select(part => new
            {
                Placeholder = ContentDecompositor.Placeholder(part.Type, part.ChunkId),
                Text = reduced.TryGetValue(part.ChunkId, out var text) ? text : part.Text,
            })
            .ToArray();

        var template = decomposition.Template;
        var composed = new StringBuilder(template.Length);
        var index = 0;

        while (index < template.Length)
        {
            var matched = false;

            foreach (var part in placeholders)
            {
                if (index + part.Placeholder.Length > template.Length)
                {
                    continue;
                }

                if (string.CompareOrdinal(template, index, part.Placeholder, 0, part.Placeholder.Length) != 0)
                {
                    continue;
                }

                composed.Append(part.Text);
                index += part.Placeholder.Length;
                matched = true;
                break;
            }

            if (matched)
            {
                continue;
            }

            composed.Append(template[index]);
            index++;
        }

        return composed.ToString();
    }
}
