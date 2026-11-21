using System.ComponentModel;
using System.Globalization;
using AiProxy.Core.Replay;
using FluentValidation;
using JetBrains.Annotations;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>What to replay and how to print it.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ReplaySettings : CommandSettings
{
    public const string BothSources = "both";
    public const string ContentSource = "content";
    public const string CaptureSource = "capture";

    [CommandOption("--minimum <TOKENS>")]
    [Description("The smallest region, in estimated tokens, that becomes a preview (default: the proxy's own).")]
    public int? Minimum { get; init; }

    [CommandOption("--budget-share <SHARE>")]
    [Description("The share of a region's reduced tokens a preview may hold, above 0 and at most 1 (default: the proxy's own).")]
    public decimal? BudgetShare { get; init; }

    [CommandOption("--minimum-saving <TOKENS>")]
    [Description("The least a preview must save over the reduced text, in estimated tokens (default: the proxy's own).")]
    public int? MinimumSaving { get; init; }

    [CommandOption("--since <DATE>")]
    [Description("Replay only requests from this date or time on (UTC).")]
    public string? Since { get; init; }

    [CommandOption("--source <SOURCE>")]
    [DefaultValue(BothSources)]
    [Description("Where to read bodies: content, capture, both.")]
    public string Source { get; init; } = BothSources;

    [CommandOption("--include-encoded")]
    [Description("Replay bodies that carry a content encoding too; the proxy never compresses them, so they are left out by default.")]
    public bool IncludeEncoded { get; init; }

    [CommandOption("--home <DIR>")]
    [Description("The proxy's data directory (defaults to Proxy__DataDirectory, then ~/.ai-proxy).")]
    public string? Home { get; init; }

    [CommandOption("--json")]
    [Description("Print the result as JSON.")]
    public bool Json { get; init; }

    /// <summary>The databases <see cref="Source" /> names.</summary>
    public ReplaySource[] Sources => Source switch
    {
        ContentSource => [ReplaySource.Content],
        CaptureSource => [ReplaySource.Capture],
        _ => [ReplaySource.Content, ReplaySource.Capture]
    };

    /// <summary>The parsed <see cref="Since" />, or null when none was given.</summary>
    public DateTimeOffset? SinceTime => Since is null ? null : DateTimeOffset.Parse(Since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    internal sealed class Validator : AbstractValidator<ReplaySettings>
    {
        public Validator()
        {
            RuleFor(static settings => settings.Minimum)
                .Must(static minimum => minimum is null or > 0)
                .WithMessage("The minimum must be a positive number of tokens.");
            RuleFor(static settings => settings.BudgetShare)
                .Must(static share => share is null or > 0 and <= 1)
                .WithMessage("The budget share must be above 0 and at most 1.");
            RuleFor(static settings => settings.MinimumSaving)
                .Must(static saving => saving is null or >= 0)
                .WithMessage("The minimum saving must not be negative.");
            RuleFor(static settings => settings.Source)
                .Must(static source => source is BothSources or ContentSource or CaptureSource)
                .WithMessage("The source must be content, capture or both.");
            RuleFor(static settings => settings.Since)
                .Must(static since => since is null || DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                .WithMessage("The since value must be a date or time.");
        }

        public static string? Error(ReplaySettings settings)
        {
            return new Validator().Validate(settings).Errors.FirstOrDefault()?.ErrorMessage;
        }
    }
}
