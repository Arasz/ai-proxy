namespace AiProxy.Core.Providers;

/// <summary>How a detection ended.</summary>
public enum DetectionOutcome
{
    /// <summary>One signal named a provider.</summary>
    Resolved,

    /// <summary>Two signals named different providers, so the request is refused rather than guessed at.</summary>
    Conflicted,

    /// <summary>No signal named a provider.</summary>
    Unresolved,
}
