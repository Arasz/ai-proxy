namespace AiProxy.Cli;

/// <summary>A command line split into what ai-proxy parses and what the harness receives unchanged.</summary>
/// <param name="Launcher">The arguments ai-proxy parses.</param>
/// <param name="Forwarded">The harness arguments, or null when the command line has none.</param>
internal sealed record HarnessArguments(string[] Launcher, string[]? Forwarded)
{
    private const string Separator = "--";

    private static readonly string[] ValueOptions = ["--proxy", "--ca-file"];

    private static readonly string[] Flags = ["-h", "--help"];

    /// <summary>Forwards everything from the first argument after the executable that is not a launcher option, or after a <c>--</c>.</summary>
    public static HarnessArguments Split(string[] args)
    {
        if (args is not [HarnessProcess.Command, ..])
        {
            return new HarnessArguments(args, null);
        }

        var executableSeen = false;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == Separator)
            {
                return new HarnessArguments(args[..index], args[(index + 1)..]);
            }

            if (ValueOptions.Contains(argument))
            {
                index++;
            }
            else if (!IsInlineOption(argument) && !Flags.Contains(argument))
            {
                if (executableSeen)
                {
                    return new HarnessArguments(args[..index], args[index..]);
                }

                executableSeen = !argument.StartsWith('-');
            }
        }

        return new HarnessArguments(args, null);
    }

    private static bool IsInlineOption(string argument)
    {
        return ValueOptions.Any(option => argument.StartsWith($"{option}=", StringComparison.Ordinal));
    }
}
