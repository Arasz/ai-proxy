using AiProxy.Doctor;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Checks the proxy setup and exits 0 (all pass), 1 (any fail) or 2 (warnings or unknowns only); changes nothing.</summary>
public sealed class DoctorCommand : AsyncCommand<DoctorSettings>
{
    public const string Command = "doctor";

    private const int OperationCancelledReturnCode = 130;

    public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings settings, CancellationToken cancellationToken)
    {
        var runtime = context.Data as GainRuntime ?? throw new InvalidOperationException("The doctor command was registered without its runtime.");
        if (DoctorSettings.Validator.Error(settings) is { } validationError)
        {
            throw new ArgumentException(validationError);
        }

        try
        {
            var variable = runtime.Variable ?? Environment.GetEnvironmentVariable;
            var report = await new DoctorRun(settings, runtime.Time, variable).RunAsync(cancellationToken).ConfigureAwait(false);
            if (settings.Format == GainFormats.Json)
            {
                await runtime.Output.WriteLineAsync(new DoctorView().Json(report)).ConfigureAwait(false);
            }
            else
            {
                runtime.Console.Write(new DoctorView().Render(report));
            }

            return report.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationCancelledReturnCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await runtime.Error.WriteLineAsync($"ai-proxy doctor: {exception.GetType().Name}").ConfigureAwait(false);
            return 1;
        }
    }
}
