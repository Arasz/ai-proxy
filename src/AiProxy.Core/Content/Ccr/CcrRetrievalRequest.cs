namespace AiProxy.Core.Content.Ccr;

/// <summary>One validated <c>proxy_retrieve</c> call.</summary>
/// <param name="Id">The preview id.</param>
/// <param name="Mode">lines, pattern or whole.</param>
/// <param name="Start">The first line asked for, 1-based; set for lines.</param>
/// <param name="End">The last line asked for, 1-based; set for lines.</param>
/// <param name="Pattern">The regular expression; set for pattern.</param>
/// <param name="Context">The lines shown around each match.</param>
public sealed record CcrRetrievalRequest(string Id, string Mode, int? Start, int? End, string? Pattern, int Context);
