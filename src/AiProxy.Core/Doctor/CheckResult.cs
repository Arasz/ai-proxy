namespace AiProxy.Core.Doctor;

/// <summary>What one check found, with a hint for fixing it when something is wrong.</summary>
/// <param name="Name">Short check name, for example <c>api</c>.</param>
/// <param name="Status">The verdict.</param>
/// <param name="Detail">One line saying what was seen.</param>
/// <param name="Fix">What to do next; null when nothing needs doing.</param>
public sealed record CheckResult(string Name, CheckStatus Status, string Detail, string? Fix = null);
