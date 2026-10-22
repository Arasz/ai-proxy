namespace AiProxy.Core.Content;

/// <summary>Puts the reduced parts back into the template they came from.</summary>
public interface ICompositor
{
    /// <summary>Composes the parts into the text the template describes.</summary>
    /// <param name="decomposition">The decomposition the parts belong to.</param>
    /// <param name="parts">The reduced parts, by chunk id.</param>
    string Compose(Decomposition decomposition, IReadOnlyList<CompressedPart> parts);
}
