namespace AiProxy.Core.Content.Ccr;

/// <summary>The line a lossy code view ends with, naming the id its exact original is kept under.</summary>
public static class CcrView
{
    /// <summary>The text every view hint starts with.</summary>
    public const string Marker = "[proxy view ";

    /// <summary>The id an original is kept under; the same id a preview of the same text gets.</summary>
    public static string IdOf(string original) => CcrText.IdOf(original);

    /// <summary>The hint line for a view whose original is kept under <paramref name="id"/>.</summary>
    public static string Hint(string id) =>
        CcrText.Invariant($"{Marker}{id}: comments and imports removed. Call proxy_retrieve with this id for the exact text.]");
}
