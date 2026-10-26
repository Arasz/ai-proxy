namespace AiProxy.Mcp.Tests;

/// <summary>The MCP server reaches the proxy only through its HTTP client, never through the proxy's storage.</summary>
public sealed class McpDependencyTests
{
    [Fact]
    public void The_server_references_the_client_and_never_the_storage()
    {
        var references = typeof(RetrievalTools).Assembly.GetReferencedAssemblies().Select(static reference => reference.Name).ToArray();

        Assert.Contains("AiProxy.Client", references);
        Assert.DoesNotContain("AiProxy.Storage", references);
        Assert.DoesNotContain(references, static name => name?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true);
    }
}
