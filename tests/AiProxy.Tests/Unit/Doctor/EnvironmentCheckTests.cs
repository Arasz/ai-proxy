using AiProxy.Core.Doctor;
using AiProxy.Doctor;

namespace AiProxy.Tests.Unit.Doctor;

public sealed class EnvironmentCheckTests
{
    private static readonly Uri Proxy = new("http://127.0.0.1:8080");

    private static Task<CheckResult> Run(Dictionary<string, string> environment)
    {
        return new EnvironmentCheck(Proxy, name => environment.GetValueOrDefault(name)).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unset_variables_pass_with_a_note_about_the_system_proxy()
    {
        var result = await Run([]);
        Assert.Equal(CheckStatus.Pass, result.Status);
        Assert.Contains("not set; clients using the system proxy are covered", result.Detail);
    }

    [Theory]
    [InlineData("HTTPS_PROXY")]
    [InlineData("https_proxy")]
    [InlineData("HTTP_PROXY")]
    [InlineData("http_proxy")]
    public async Task A_variable_naming_the_proxy_passes(string name)
    {
        var result = await Run(new() { [name] = "http://localhost:8080" });
        Assert.Equal(CheckStatus.Pass, result.Status);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9999")]
    [InlineData("http://proxy.corp:8080")]
    public async Task A_variable_naming_another_endpoint_fails(string value)
    {
        var result = await Run(new() { ["HTTPS_PROXY"] = value });
        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.NotNull(result.Fix);
    }

    [Fact]
    public async Task Credentials_and_query_never_reach_the_result()
    {
        var result = await Run(new() { ["HTTPS_PROXY"] = "http://u:SENTINEL@127.0.0.1:9999/?k=SENTINEL2", ["http_proxy"] = "http://u:SENTINEL@127.0.0.1:8080/?k=SENTINEL2" });
        var text = result.Detail + result.Fix;
        Assert.DoesNotContain("SENTINEL", text);
        Assert.Contains("http://127.0.0.1:9999", text);
    }
}
