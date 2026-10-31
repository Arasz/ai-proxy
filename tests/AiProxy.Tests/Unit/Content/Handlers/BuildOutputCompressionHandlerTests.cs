using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>The MSBuild reducer: every diagnostic survives whole, the tool invocations shrink.</summary>
public sealed class BuildOutputCompressionHandlerTests
{
    private static readonly BuildOutputCompressionHandler Handler = new();

    // A compiler error whose message names a long generic type, as CS1503 and CS0029 do.
    private static readonly string LongDiagnostic =
        $"/src/App/Program.cs(12,9): error CS1503: Argument 1: cannot convert from '{string.Join(", ", Enumerable.Repeat("System.Collections.Generic.Dictionary<string, int>", 40))}' to 'int' [/src/App/App.csproj]";

    public static TheoryData<string> BuildLogs()
    {
        return new TheoryData<string>(
            Payload("dotnet_build_failure"),
            Payload("dotnet_build_failure_verbose"),
            string.Join("\n", "  Determining projects to restore...", "  All projects are up-to-date for restore.", LongDiagnostic, "", "Build FAILED.", "", LongDiagnostic, "    0 Warning(s)", "    1 Error(s)"));
    }

    private static string Payload(string name)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ReducerScoring", "payloads", $"{name}.txt"));
    }

    private static ContentPart Part(string text)
    {
        return new ContentPart("request", 1, ContentType.BuildOutput, text);
    }

    private static bool IsDiagnostic(string line)
    {
        return line.Contains("): error ", StringComparison.Ordinal) || line.Contains("): warning ", StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BuildLogs))]
    public async Task Every_diagnostic_line_survives_verbatim(string text)
    {
        var expected = text.Split('\n').Where(IsDiagnostic).ToArray();
        Assert.NotEmpty(expected);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        var kept = result.Text.Split('\n');
        Assert.All(expected, line => Assert.Contains(line, kept));
    }

    [Fact]
    public async Task The_verbose_build_log_shrinks_to_a_tenth()
    {
        var text = Payload("dotnet_build_failure_verbose");

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Equal(BuildOutputCompressionHandler.HandlerPath, result.Path);
        Assert.True(result.Text.Length < text.Length / 10, $"{result.Text.Length} of {text.Length} characters kept");
    }

    [Theory]
    [InlineData("Build FAILED.")]
    [InlineData("    0 Warning(s)")]
    [InlineData("    2 Error(s)")]
    [InlineData("Time Elapsed 00:00:00.42")]
    public async Task The_summary_lines_survive(string line)
    {
        var result = await Handler.HandleAsync(Part(Payload("dotnet_build_failure_verbose")), CancellationToken.None);

        Assert.Contains(line, result.Text.Split('\n'));
    }

    [Fact]
    public async Task A_long_invocation_keeps_its_head_and_names_what_it_dropped()
    {
        var invocation = "  csc /noconfig " + string.Join(" ", Enumerable.Repeat("/reference:/packs/Microsoft.NETCore.App.Ref/ref/net10.0/System.Runtime.dll", 60));
        var text = string.Join("\n", "CoreCompile:", invocation, "Build succeeded.");

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        var shortened = Assert.Single(result.Text.Split('\n'), line => line.StartsWith("  csc /noconfig ", StringComparison.Ordinal));
        Assert.EndsWith($"[{invocation.Length - BuildOutputCompressionHandler.HeadLength} characters dropped]", shortened, StringComparison.Ordinal);
        Assert.StartsWith(invocation[..BuildOutputCompressionHandler.HeadLength], shortened, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_line_of_prose_beside_build_output_is_kept_whole()
    {
        var paragraph = string.Join(" ", Enumerable.Repeat("The release notes explain why the build was changed and what the team decided about the warnings.", 8));
        var text = string.Join("\n", paragraph, "/src/App/Program.cs(3,5): error CS0029: Cannot implicitly convert type 'string' to 'int' [/src/App/App.csproj]", "Build FAILED.");

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Contains(paragraph, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exact_repeat_of_a_diagnostic_is_kept_once()
    {
        var text = Payload("dotnet_build_failure");
        var diagnostic = text.Split('\n').First(IsDiagnostic);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Single(result.Text.Split('\n'), line => line == diagnostic);
    }

    [Fact]
    public async Task Build_output_the_msbuild_rules_cannot_shorten_goes_through_the_log_reducer()
    {
        var text = string.Join("\n", "  Determining projects to restore...", "  All projects are up-to-date for restore.", "  App -> /src/App/bin/Debug/net10.0/App.dll");

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Equal(LogCompressionHandler.HandlerPath, result.Path);
    }

    [Fact]
    public void Build_output_is_worth_compressing_from_the_log_minimum()
    {
        Assert.Equal(CompressionOptions.Default.MinimumFor(ContentType.Log), CompressionOptions.Default.MinimumFor(ContentType.BuildOutput));
    }

    [Fact]
    public void It_claims_build_output_only()
    {
        Assert.True(Handler.CanHandle(ContentType.BuildOutput));
        Assert.False(Handler.CanHandle(ContentType.Log));
    }
}
