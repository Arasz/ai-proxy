namespace AiProxy.Core.Content.Ccr;

/// <summary>A parsed <c>proxy_retrieve</c> call: the validated request, or the refusal the model reads.</summary>
public readonly union CcrParseResult(CcrRetrievalRequest, CcrRefusal);

/// <summary>Malformed arguments, with the reason the model reads.</summary>
/// <param name="Reason">Why the arguments were refused.</param>
public sealed record CcrRefusal(string Reason);
