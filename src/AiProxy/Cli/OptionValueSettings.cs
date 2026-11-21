using Spectre.Console;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Settings that reject a value-taking option left without a value at the end of the line.</summary>
public abstract class OptionValueSettings : CommandSettings
{
    /// <summary>The text Spectre binds to a value-taking option that ends the line with no value.</summary>
    private const string MissingValue = "__default_command";

    /// <inheritdoc />
    public override ValidationResult Validate()
    {
        foreach (var property in GetType().GetProperties())
        {
            if (property.PropertyType == typeof(string)
                && property.GetValue(this) as string == MissingValue
                && property.GetCustomAttributes(typeof(CommandOptionAttribute), true) is [CommandOptionAttribute option, ..])
            {
                return ValidationResult.Error($"Option '--{option.LongNames.FirstOrDefault()}' needs a value.");
            }
        }

        return ValidationResult.Success();
    }
}
