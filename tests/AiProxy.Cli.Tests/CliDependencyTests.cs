namespace AiProxy.Cli.Tests;

/// <summary>The CLI reaches the proxy only through its HTTP client, never through the proxy's storage.</summary>
public sealed class CliDependencyTests
{
    [Fact]
    public void The_cli_references_the_client_and_never_the_storage()
    {
        var references = typeof(GainCommand).Assembly.GetReferencedAssemblies().Select(static reference => reference.Name).ToArray();

        Assert.Contains("AiProxy.Client", references);
        Assert.DoesNotContain("AiProxy.Storage", references);
        Assert.DoesNotContain(references, static name => name?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true);
    }
}
