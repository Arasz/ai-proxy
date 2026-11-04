namespace AiProxy.Core.Doctor;

/// <summary>All check results, with the overall status and the process exit code they map to.</summary>
public sealed record DoctorReport(IReadOnlyList<CheckResult> Checks)
{
    /// <summary>Fail beats Warn and Unknown; those beat Pass. Warn and Unknown tie, and the first seen wins.</summary>
    public CheckStatus Status
    {
        get
        {
            if (Checks.Any(static check => check.Status == CheckStatus.Fail))
            {
                return CheckStatus.Fail;
            }

            return Checks.Select(static check => check.Status).FirstOrDefault(static status => status is CheckStatus.Warn or CheckStatus.Unknown);
        }
    }

    /// <summary>0 when every check passes, 1 when any fails, 2 when some warn or are unknown but none fail.</summary>
    public int ExitCode => Status switch
    {
        CheckStatus.Pass => 0,
        CheckStatus.Fail => 1,
        _ => 2
    };
}
