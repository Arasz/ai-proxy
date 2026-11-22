using AiProxy.Cli;
using AiProxy.Core.Doctor;
using AiProxy.Hosting.Proxy;

namespace AiProxy.Doctor;

/// <summary>Warns when the running proxy's last capture write failed within the last hour, or when capture is off though it is configured on.</summary>
internal sealed class CaptureCheck(string dataDirectory, TimeProvider time, bool configuredOn)
{
    public const string Name = "capture";

    private static readonly TimeSpan RecentError = TimeSpan.FromHours(1);

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var reading = await ProxyControl.ReadOwnerAsync(dataDirectory, cancellationToken).ConfigureAwait(false);
        if (reading is not { State: ProxyControl.OwnerState.Running, Owner: { } owner })
        {
            return new CheckResult(Name, CheckStatus.Pass, "not applicable: no ai-proxy answered on the control pipe.");
        }

        if (owner.Capture is not { } capture)
        {
            return new CheckResult(Name, CheckStatus.Pass, "not applicable: the running proxy does not report capture state.");
        }

        if (capture.State is { LastWriteError: { } error, LastWriteErrorAt: { } failedAt } && time.GetUtcNow() - failedAt <= RecentError)
        {
            return new CheckResult(Name, CheckStatus.Warn, $"the last capture write failed at {CaptureText.Time(failedAt)}: {error}") { Fix = "Check the free disk space and the permissions of capture.db, and read proxy.log." };
        }

        if (!capture.Enabled)
        {
            return configuredOn
                ? new CheckResult(Name, CheckStatus.Warn, "capture is off in the running proxy, but Proxy__Capture asks for it on.") { Fix = "Restart ai-proxy so it reads the setting." }
                : new CheckResult(Name, CheckStatus.Pass, "off.");
        }

        return new CheckResult(Name, CheckStatus.Pass, capture.State is { } state ? $"on, {CaptureText.Size(state)}." : "on.");
    }
}
