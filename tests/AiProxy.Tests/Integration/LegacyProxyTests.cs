using AiProxy.Hosting;

namespace AiProxy.Tests.Integration;

public sealed class LegacyProxyTests
{
    [Fact]
    public async Task Failed_operating_system_discovery_is_not_treated_as_an_empty_inventory()
    {
        var executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows() ? ["/c", "exit", "42"] : ["-c", "exit 42"];
        await Assert.ThrowsAsync<IOException>(() => LegacyProxy.ExecuteAsync(executable, arguments, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(123, "native:1", 124, "native:1")]
    [InlineData(123, "native:1", 123, "native:2")]
    [InlineData(123, "", 123, "")]
    [InlineData(0, "native:1", 0, "native:1")]
    public void A_changed_or_missing_process_identity_does_not_authorize_a_signal(int expectedPid, string expectedBirth, int actualPid, string actualBirth)
    {
        Assert.False(LegacyProxy.MatchesIdentity(expectedPid, expectedBirth, actualPid, actualBirth));
    }

    [Fact]
    public void A_loaded_product_reference_does_not_make_the_test_host_a_proxy_entry()
    {
        Assert.False(LegacyProxy.IsEntryAssembly(typeof(LegacyProxyTests).Assembly.Location));
        Assert.False(LegacyProxy.IsNativeEntry(Path.Combine(AppContext.BaseDirectory, "AiProxy.Tests")));
        Assert.True(LegacyProxy.IsEntryAssembly(typeof(ProxyApplication).Assembly.Location));
    }

    [Theory]
    [InlineData(new[] { "dotnet", "AiProxy.dll" }, "AiProxy.dll")]
    [InlineData(new[] { "dotnet", "exec", "--runtimeconfig", "settings.json", "AiProxy.dll" }, "AiProxy.dll")]
    [InlineData(new[] { "dotnet", "Other.dll", "AiProxy.dll" }, null)]
    [InlineData(new[] { "dotnet", "exec", "--unknown", "AiProxy.dll" }, null)]
    public void Managed_identity_uses_only_the_entry_assembly_and_the_target_working_directory(string[] arguments, string? expected)
    {
        var targetDirectory = Path.Combine(Path.GetTempPath(), "legacy-target");
        Assert.Equal(expected is null ? null : Path.Combine(targetDirectory, expected), LegacyProxy.ManagedEntry(arguments, targetDirectory));
    }

    [Fact]
    public void A_relative_managed_entry_without_a_verified_target_directory_is_rejected()
    {
        Assert.Null(LegacyProxy.ManagedEntry(["dotnet", "AiProxy.dll"], null));
    }

    [Fact]
    public void Renaming_an_unrelated_assembly_to_the_product_name_does_not_identify_the_product()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var renamed = Path.Combine(directory.Path, "AiProxy.dll");
        File.Copy(typeof(LegacyProxyTests).Assembly.Location, renamed);
        Assert.False(LegacyProxy.IsEntryAssembly(renamed));
        Assert.False(LegacyProxy.IsNativeEntry(Path.Combine(directory.Path, "AiProxy")));
    }

    [Theory]
    [InlineData("/Users/example/.dotnet/tools/.store/.stage/old/ai-proxy.osx-arm64/0.16.1/tools/net10.0/osx-arm64/AiProxy", true)]
    [InlineData("/tmp/unrelated/AiProxy", false)]
    [InlineData("/Users/example/.dotnet/tools/.store/.stage/old/unrelated/0.16.1/tools/net10.0/osx-arm64/AiProxy", false)]
    public void A_deleted_tool_image_requires_the_product_payload_family(string executable, bool expected)
    {
        Assert.Equal(expected, LegacyProxy.IsNativeEntry(executable));
    }
}
