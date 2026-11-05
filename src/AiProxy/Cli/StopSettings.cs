using System.ComponentModel;
using JetBrains.Annotations;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Which instance to stop: the one owning this data directory.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class StopSettings : CommandSettings
{
    [CommandOption("--data-directory <PATH>")]
    [Description("The proxy's data directory (defaults to Proxy__DataDirectory, then ~/.ai-proxy).")]
    public string? DataDirectory { get; init; }
}
