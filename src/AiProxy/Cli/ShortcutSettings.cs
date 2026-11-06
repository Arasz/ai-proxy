using System.ComponentModel;
using JetBrains.Annotations;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Whether to create or remove the shortcut.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ShortcutSettings : CommandSettings
{
    [CommandOption("--remove")]
    [Description("Remove the shortcut instead of creating it.")]
    public bool Remove { get; init; }
}
