using AiProxy.Hosting;

namespace AiProxy.Tests.Unit;

public sealed class ProcessIdentityTests
{
    [Fact]
    public void Linux_start_identity_uses_field_22_after_a_command_containing_spaces_and_closing_parentheses()
    {
        var stat = "123 (agent ) name with spaces) S " + string.Join(' ', Enumerable.Repeat("0", 18)) + " 987654321 12 34";
        Assert.Equal("linux:987654321", ProcessIdentity.ParseLinuxStat(stat));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123 agent S 1 2")]
    [InlineData("123 (agent) S 1 2")]
    public void An_unavailable_kernel_start_identity_fails_closed(string stat)
    {
        Assert.Throws<IOException>(() => ProcessIdentity.ParseLinuxStat(stat));
    }
}
