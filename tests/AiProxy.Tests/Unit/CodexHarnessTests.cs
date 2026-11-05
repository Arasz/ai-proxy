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
    [InlineData("agents")]
    [InlineData("queue", "msg")]
    [InlineData("-c", "k=v", "app-server")]
    [InlineData("--no-daemon", "exec", "hi")]
    [InlineData("--remote", "ws://x")]
    [InlineData("--remote=ws://x", "exec")]
    public void Codex_commands_that_need_the_daemon_or_choose_one_are_left_alone(params string[] arguments)
    {
        Assert.Equal(arguments, CodexHarness.LaunchArguments("codex", arguments));
    }

    public static TheoryData<string[]> ModelRuns => new()
    {
        { ["-c", "k=v", "exec", "hi"] },
        { ["--model", "o3", "-C", "/x", "exec"] },
        { ["fix it"] },
        { ["-m", "o3", "fix it"] },
        { ["--oss"] },
        { ["resume", "--last"] },
        { ["e", "hi"] }
    };

    [Theory]
    [MemberData(nameof(ModelRuns))]
    public void Codex_runs_that_start_a_model_session_get_no_daemon(string[] arguments)
    {
        Assert.Equal(["--no-daemon", .. arguments], CodexHarness.LaunchArguments("codex", arguments));
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
