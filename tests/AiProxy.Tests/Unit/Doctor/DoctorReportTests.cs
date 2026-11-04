using AiProxy.Core.Doctor;

namespace AiProxy.Tests.Unit.Doctor;

public sealed class DoctorReportTests
{
    private static CheckResult Result(CheckStatus status) => new("check", status, "detail");

    [Fact]
    public void All_passing_checks_exit_zero()
    {
        var report = new DoctorReport([Result(CheckStatus.Pass), Result(CheckStatus.Pass)]);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(CheckStatus.Pass, report.Status);
    }

    [Fact]
    public void Any_failure_exits_one_even_beside_warnings()
    {
        var report = new DoctorReport([Result(CheckStatus.Pass), Result(CheckStatus.Warn), Result(CheckStatus.Unknown), Result(CheckStatus.Fail)]);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(CheckStatus.Fail, report.Status);
    }

    [Theory]
    [InlineData(CheckStatus.Warn)]
    [InlineData(CheckStatus.Unknown)]
    public void Warnings_and_unknowns_without_failure_exit_two(CheckStatus status)
    {
        var report = new DoctorReport([Result(CheckStatus.Pass), Result(status)]);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(status, report.Status);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://user:SENTINEL@127.0.0.1:8080/path?k=SENTINEL2", "http://127.0.0.1:8080")]
    [InlineData("localhost:8080", "http://localhost:8080")]
    [InlineData("not a url at all SENTINEL", "(unparseable)")]
    public void Endpoint_text_keeps_only_scheme_host_and_port(string value, string expected)
    {
        Assert.Equal(expected, EndpointText.Safe(value));
    }
}
