namespace AiProxy.Core.Doctor;

/// <summary>The outcome of one setup check, ordered from best to worst except <see cref="Unknown" />.</summary>
public enum CheckStatus
{
    /// <summary>The check found nothing wrong.</summary>
    Pass,
    /// <summary>Something is degraded but traffic still works.</summary>
    Warn,
    /// <summary>The setup is broken.</summary>
    Fail,
    /// <summary>The check could not reach a verdict.</summary>
    Unknown
}
