using AiProxy.Cli;
using AiProxy.Client;
using Spectre.Console.Testing;

namespace AiProxy.Tests.Unit.Cli;

/// <summary>An invalid CLI option makes the settings check throw <see cref="ArgumentException"/> with the validator's message.</summary>
public sealed class SettingsCheckTests
{
    [Fact]
    public void Doctor_check_throws_the_validator_message_for_a_bad_format()
    {
        var exception = Assert.Throws<ArgumentException>(() => DoctorSettings.Validator.Check(new DoctorSettings { Format = "xml" }));

        Assert.Equal("Output format must be text or json.", exception.Message);
    }

    [Fact]
    public void Status_check_throws_the_validator_message_for_a_bad_format()
    {
        var exception = Assert.Throws<ArgumentException>(() => StatusSettings.Validator.Check(new StatusSettings { Format = "xml" }));

        Assert.Equal("Output format must be text or json.", exception.Message);
    }

    [Fact]
    public void Replay_check_throws_the_validator_message_for_a_bad_source()
    {
        var exception = Assert.Throws<ArgumentException>(() => ReplaySettings.Validator.Check(new ReplaySettings { Source = "nowhere" }));

        Assert.Equal("The source must be content, capture or both.", exception.Message);
    }

    [Fact]
    public void Gain_check_throws_the_validator_message_for_a_bad_format()
    {
        using var http = new HttpClient();
        var runtime = new GainRuntime(new TestConsole(), new StringWriter(), new StringWriter(), new ProxyClient(http), "/work", false, TimeProvider.System);

        var exception = Assert.Throws<ArgumentException>(() => GainSettings.Validator.Check(new GainSettings { Format = "xml" }, runtime));

        Assert.Equal("Output format must be text, json or csv.", exception.Message);
    }

    [Fact]
    public void Valid_settings_pass_every_check()
    {
        using var http = new HttpClient();
        var runtime = new GainRuntime(new TestConsole(), new StringWriter(), new StringWriter(), new ProxyClient(http), "/work", false, TimeProvider.System);

        DoctorSettings.Validator.Check(new DoctorSettings());
        StatusSettings.Validator.Check(new StatusSettings());
        ReplaySettings.Validator.Check(new ReplaySettings());
        GainSettings.Validator.Check(new GainSettings(), runtime);
    }
}
