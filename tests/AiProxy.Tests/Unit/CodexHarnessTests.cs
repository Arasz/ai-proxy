namespace AiProxy.Tests.Unit;

public sealed class CodexHarnessTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("/usr/local/bin/codex")]
    [InlineData("codex.cmd")]
    [InlineData("codex.exe")]
    [InlineData("CODEX")]
    public void Codex_is_recognized_by_its_file_name(string executable)
    {
        Assert.True(CodexHarness.IsCodex(executable));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex-helper")]
    [InlineData("/opt/codex/claude")]
    public void Other_executables_are_not_codex(string executable)
    {
        Assert.False(CodexHarness.IsCodex(executable));
    }

    [Fact]
    public void Codex_gets_no_daemon_before_the_users_arguments()
    {
        Assert.Equal(["--no-daemon", "exec", "hi"], CodexHarness.LaunchArguments("codex", ["exec", "hi"]));
    }

    [Fact]
    public void Codex_without_arguments_gets_only_no_daemon()
    {
        Assert.Equal(["--no-daemon"], CodexHarness.LaunchArguments("codex", []));
    }

    [Fact]
    public void Other_executables_keep_their_arguments()
    {
        Assert.Equal(["exec", "hi"], CodexHarness.LaunchArguments("claude", ["exec", "hi"]));
    }

    [Theory]
    [InlineData("remote-control", "start")]
    [InlineData("app-server", "--listen", "stdio://")]
    [InlineData("exec", "--no-daemon", "hi")]
    [InlineData("--remote", "ws://host:1")]
    [InlineData("exec", "--remote=ws://host:1")]
    public void Codex_arguments_that_already_decide_the_daemon_are_left_alone(params string[] arguments)
    {
        Assert.Equal(arguments, CodexHarness.LaunchArguments("codex", arguments));
    }

    [Fact]
    public void A_later_prompt_argument_naming_app_server_does_not_skip()
    {
        Assert.Equal(["--no-daemon", "exec", "app-server"], CodexHarness.LaunchArguments("codex", ["exec", "app-server"]));
    }

    [Fact]
    public void A_later_prompt_argument_naming_remote_control_does_not_skip()
    {
        Assert.Equal(["--no-daemon", "exec", "remote-control"], CodexHarness.LaunchArguments("codex", ["exec", "remote-control"]));
    }
}
