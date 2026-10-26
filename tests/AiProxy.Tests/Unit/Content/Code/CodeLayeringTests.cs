using AiProxy.Code;
using AiProxy.Core.Content.Code;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class CodeLayeringTests
{
    [Fact]
    public void Core_does_not_reference_a_parser_sdk()
    {
        var references = typeof(CodeReducer).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => reference.Name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
        Assert.DoesNotContain(references, reference => reference.Name == "TreeSitter");
    }

    [Fact]
    public void Adapter_project_is_where_the_parser_sdks_are_referenced()
    {
        var references = typeof(PythonCodeLexer).Assembly.GetReferencedAssemblies();

        Assert.Contains(references, reference => reference.Name == "TreeSitter");
    }
}