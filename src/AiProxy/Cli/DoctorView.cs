using System.Text.Json;
using AiProxy.Core.Doctor;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AiProxy.Cli;

/// <summary>Renders a doctor report as a table or as JSON.</summary>
public sealed class DoctorView
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IRenderable Render(DoctorReport report)
    {
        var table = new Table().AddColumns("Check", "Status", "Detail");
        foreach (var check in report.Checks)
        {
            var detail = check.Fix is null ? check.Detail : $"{check.Detail}\nFix: {check.Fix}";
            table.AddRow(Markup.Escape(check.Name), Markup.Escape(Label(check.Status)), Markup.Escape(detail));
        }

        return table;
    }

    public string Json(DoctorReport report)
    {
        return JsonSerializer.Serialize(new
        {
            status = Label(report.Status),
            exitCode = report.ExitCode,
            checks = report.Checks.Select(static check => new { name = check.Name, status = Label(check.Status), detail = check.Detail, fix = check.Fix })
        }, JsonOptions);
    }

    private static string Label(CheckStatus status)
    {
        return status.ToString().ToLowerInvariant();
    }
}
