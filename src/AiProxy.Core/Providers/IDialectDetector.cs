namespace AiProxy.Core.Providers;

/// <summary>Names the provider dialect a request speaks, from the signals one detector reads.</summary>
public interface IDialectDetector
{
    /// <summary>Detects the dialect, or returns null when this detector finds no signal.</summary>
    Detection? Detect(DetectionRequest request);
}
