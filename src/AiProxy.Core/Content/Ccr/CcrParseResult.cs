namespace AiProxy.Core.Content.Ccr;

/// <summary>A parsed <c>proxy_retrieve</c> call: the request, or null and the reason the arguments were refused.</summary>
/// <param name="Request">The validated request, or null when the arguments are malformed.</param>
/// <param name="Reason">Why the arguments were refused; empty on success.</param>
public sealed record CcrParseResult(CcrRetrievalRequest? Request, string Reason)
{
    /// <summary>A valid call.</summary>
    public static CcrParseResult Accepted(CcrRetrievalRequest request)
    {
        return new CcrParseResult(request, "");
    }

    /// <summary>Malformed arguments, with the reason the model reads.</summary>
    public static CcrParseResult Refused(string reason)
    {
        return new CcrParseResult(null, reason);
    }
}