using AiProxy.Cli;

namespace AiProxy.Tests.Unit.Cli;

public sealed class EntryPointTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--restart")]
    [InlineData("--restart|--Proxy:Port|8090")]
    [InlineData("--Proxy:Port=8090")]
    [InlineData("--Proxy:Port|8090|--urls=http://127.0.0.1:1")]
    [InlineData("--Proxy:Port=")]
    public void Valid_configuration_forms_start_the_proxy(string arguments)
    {
        Assert.Equal(EntryRoute.Serve, EntryPoint.Route(Split(arguments)));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("--restart|bogus")]
    [InlineData("--flag")]
    [InlineData("--Proxy:Port=1|--flag")]
    [InlineData("--flag|--Proxy:Port=1")]
    [InlineData("--Proxy:Port|8090|stray")]
    public void Unknown_command_or_bare_flag_is_usage(string arguments)
    {
        Assert.Equal(EntryRoute.Usage, EntryPoint.Route(Split(arguments)));
    }

    [Fact]
    public void Guard_argument_vector_is_guard()
    {
        Assert.Equal(EntryRoute.Guard, EntryPoint.Route(["--guard-system-proxy", "/tmp/data"]));
    }

    [Theory]
    [InlineData("gain")]
    [InlineData("mcp|--endpoint|http://127.0.0.1:1")]
    [InlineData("--help")]
    [InlineData("--version")]
    public void Commands_help_and_version_are_command(string arguments)
    {
        Assert.Equal(EntryRoute.Command, EntryPoint.Route(Split(arguments)));
    }

    [Fact]
    public async Task Version_prints_the_product_version()
    {
        using var fixture = new GainCommandTests.Fixture();
        var exit = await new CliApplication(fixture.Runtime).RunAsync(["--version"], TestContext.Current.CancellationToken);
        Assert.Equal(0, exit);
        Assert.Equal(EntryPoint.ProductVersion, fixture.Output.ToString().Trim());
        Assert.Matches(@"^\d+\.\d+\.\d+$", EntryPoint.ProductVersion);
    }

    private static string[] Split(string arguments) => arguments.Length == 0 ? [] : arguments.Split('|');
}
