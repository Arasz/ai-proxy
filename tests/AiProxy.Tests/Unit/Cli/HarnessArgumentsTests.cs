using AiProxy.Cli;

namespace AiProxy.Tests.Unit.Cli;

public sealed class HarnessArgumentsTests
{
    [Theory]
    [InlineData("harness claude --resume abc", "harness claude", "--resume abc")]
    [InlineData("harness claude --model opus --proxy x", "harness claude", "--model opus --proxy x")]
    [InlineData("harness codex exec prompt", "harness codex", "exec prompt")]
    [InlineData("harness --proxy http://p --ca-file c claude --resume abc", "harness --proxy http://p --ca-file c claude", "--resume abc")]
    [InlineData("harness claude --proxy http://p --resume abc", "harness claude --proxy http://p", "--resume abc")]
    [InlineData("harness claude --proxy=http://p --resume abc", "harness claude --proxy=http://p", "--resume abc")]
    [InlineData("harness claude -- --help", "harness claude", "--help")]
    [InlineData("harness claude --resume abc -- tail", "harness claude", "--resume abc -- tail")]
    public void Everything_after_the_executable_that_is_not_a_launcher_option_goes_to_the_harness(string args, string launcher, string forwarded)
    {
        var split = HarnessArguments.Split(args.Split(' '));

        Assert.Equal(launcher.Split(' '), split.Launcher);
        Assert.Equal(forwarded.Split(' '), split.Forwarded);
    }

    [Theory]
    [InlineData("harness claude")]
    [InlineData("harness claude --proxy http://p")]
    [InlineData("harness claude --help")]
    [InlineData("harness --help")]
    [InlineData("harness --unknown claude")]
    [InlineData("gain --resume abc")]
    public void Launcher_only_commands_forward_nothing(string args)
    {
        var split = HarnessArguments.Split(args.Split(' '));

        Assert.Equal(args.Split(' '), split.Launcher);
        Assert.Null(split.Forwarded);
    }
}
