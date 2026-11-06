namespace AiProxy.Core.Gain;

/// <summary>Maps a ledger provider to the harness its traffic is reported under; the ledger does not record the client itself.</summary>
public static class GainHarness
{
    public const string Dimension = "harness";
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Other = "other";

    /// <summary>Every harness, in display order.</summary>
    public static readonly IReadOnlyList<string> Names = [Claude, Codex, Other];

    /// <summary>The harness of a provider; any provider that is not Anthropic or OpenAI is <see cref="Other"/>.</summary>
    public static string Of(string? provider)
    {
        return provider switch
        {
            "anthropic" => Claude,
            "openai" => Codex,
            _ => Other
        };
    }
}
