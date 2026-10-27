using AiProxy.Hosting;
using AiProxy.Hosting.Guard;

namespace AiProxy.Tests.Unit;

public sealed class ProcessSystemProxyGuardLauncherTests
{
    [Theory]
    [InlineData("/usr/local/bin/ai-proxy")]
    [InlineData("C:\\tools\\ai-proxy.exe")]
    public void An_apphost_runs_itself_with_the_guard_arguments(string processPath)
    {
        var arguments = ProcessSystemProxyGuardLauncher.BuildArguments(processPath, "/app/AiProxy.dll", "/data");

        Assert.Equal(["--guard-system-proxy", "/data"], arguments);
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("C:\\Program Files\\dotnet\\dotnet.exe")]
    public void The_dotnet_muxer_is_given_the_assembly_first(string processPath)
    {
        var arguments = ProcessSystemProxyGuardLauncher.BuildArguments(processPath, "/app/AiProxy.dll", "/data");

        Assert.Equal(["/app/AiProxy.dll", "--guard-system-proxy", "/data"], arguments);
    }
}