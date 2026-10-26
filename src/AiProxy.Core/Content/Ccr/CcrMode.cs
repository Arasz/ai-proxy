namespace AiProxy.Core.Content.Ccr;

/// <summary>Whether a request gets previews, and where its originals must be readable from.</summary>
public enum CcrMode
{
    /// <summary>No previews.</summary>
    Off,

    /// <summary>Previews whose originals are kept in the store before they are sent, because later requests carry only ids.</summary>
    Thread,

    /// <summary>Previews whose originals are in every request the client sends.</summary>
    Stateless
}