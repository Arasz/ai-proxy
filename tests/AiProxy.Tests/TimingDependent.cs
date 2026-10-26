namespace AiProxy.Tests;

/// <summary>The skip reason for tests whose result depends on wall-clock time; search for it to re-enable them.</summary>
internal static class TimingDependent
{
    public const string Reason = "Timing-dependent: the result depends on wall-clock durations, which vary on CI runners.";
}