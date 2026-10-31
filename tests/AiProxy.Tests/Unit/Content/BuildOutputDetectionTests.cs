using AiProxy.Core.Content;

namespace AiProxy.Tests.Unit.Content;

/// <summary>Build output is its own kind, and finding it does not take what belongs to another kind.</summary>
public sealed class BuildOutputDetectionTests
{
    private static readonly ContentDecompositor Decompositor = new();

    private static string Payload(string name)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ReducerScoring", "payloads", $"{name}.txt"));
    }

    [Theory]
    [InlineData("dotnet_build_failure")]
    [InlineData("dotnet_build_failure_verbose")]
    public void A_build_log_is_one_build_output_part(string payload)
    {
        var decomposition = Decompositor.Decompose(Payload(payload), "request");

        Assert.Equal(ContentType.BuildOutput, Assert.Single(decomposition.Parts).Type);
    }

    [Fact]
    public void The_progress_lines_around_a_build_summary_belong_to_the_build_output()
    {
        var text = string.Join("\n", "  Determining projects to restore...", "  All projects are up-to-date for restore.", "  App -> /src/App/bin/Debug/net10.0/App.dll", "", "Build succeeded.", "    0 Warning(s)", "    0 Error(s)", "", "Time Elapsed 00:00:01.20");

        var decomposition = Decompositor.Decompose(text, "request");

        Assert.Equal(ContentType.BuildOutput, Assert.Single(decomposition.Parts).Type);
    }

    [Theory]
    [InlineData("agent_log")]
    [InlineData("rtk_proxy_log")]
    public void A_log_stays_a_log(string payload)
    {
        var decomposition = Decompositor.Decompose(Payload(payload), "request");

        Assert.All(decomposition.Parts, part => Assert.Equal(ContentType.Log, part.Type));
    }

    // The new kind is the only thing detection added, so a part that is not build output kept the kind it had.
    [Theory]
    [InlineData("incident_report")]
    [InlineData("prose_large")]
    [InlineData("dotnet_test_failure")]
    [InlineData("dotnet_test_failures")]
    [InlineData("code_file")]
    [InlineData("json_payload")]
    [InlineData("dir_listing")]
    public void Other_text_is_not_build_output(string payload)
    {
        var decomposition = Decompositor.Decompose(Payload(payload), "request");

        Assert.DoesNotContain(decomposition.Parts, part => part.Type == ContentType.BuildOutput);
    }

    [Fact]
    public void A_numbered_error_without_a_code_prefix_is_not_a_diagnostic()
    {
        Assert.False(MsBuildLines.IsDiagnostic("Step 3: error 404: the page was not found"));
        Assert.True(MsBuildLines.IsDiagnostic("Step.cs(3,1): error CS0404: the page was not found"));
    }

    [Fact]
    public void A_sentence_that_mentions_a_build_is_prose()
    {
        var decomposition = Decompositor.Decompose("The build succeeded after the fix, with 0 warnings and 1 error left to look at.", "request");

        Assert.Equal(ContentType.Prose, Assert.Single(decomposition.Parts).Type);
    }
}
