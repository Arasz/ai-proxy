using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AiProxy.Generators.Tests;

public sealed class RedundantNullCheckAnalyzerTests
{
    [Theory]
    [InlineData("string x", "x")]
    [InlineData("object x", "x")]
    [InlineData("System.Collections.Generic.List<int> x", "x")]
    public async Task Null_check_on_a_non_nullable_parameter_is_an_error(string parameter, string name)
    {
        var diagnostics = await Analyze($"class C {{ void M({parameter}) {{ System.ArgumentNullException.ThrowIfNull({name}); }} }}");
        var error = Assert.Single(diagnostics);
        Assert.Equal("APGEN003", error.Id);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public async Task Null_check_on_a_non_nullable_field_is_an_error()
    {
        var diagnostics = await Analyze("class C { private readonly string _x = \"\"; void M() { System.ArgumentNullException.ThrowIfNull(_x); } }");
        Assert.Equal("APGEN003", Assert.Single(diagnostics).Id);
    }

    [Theory]
    [InlineData("class C { void M(string? x) { System.ArgumentNullException.ThrowIfNull(x); } }")]
    [InlineData("class C { private string? _x; void M() { System.ArgumentNullException.ThrowIfNull(_x); } }")]
    [InlineData("class C { void M(object? x) { System.ArgumentNullException.ThrowIfNull(x); } }")]
    [InlineData("class C { void M<T>(T x) { System.ArgumentNullException.ThrowIfNull(x); } }")]
    [InlineData("class C { void M(int x) { System.ArgumentNullException.ThrowIfNull(x); } }")]
    [InlineData("class C { void M(string x) { System.ArgumentException.ThrowIfNullOrWhiteSpace(x); } }")]
    public async Task Null_check_on_a_nullable_or_unconstrained_value_is_allowed(string source)
    {
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Null_check_where_nullable_context_is_off_is_allowed()
    {
        Assert.Empty(await Analyze("class C { void M(string x) { System.ArgumentNullException.ThrowIfNull(x); } }", NullableContextOptions.Disable));
    }

    private static async Task<IReadOnlyList<Diagnostic>> Analyze(string source, NullableContextOptions nullable = NullableContextOptions.Enable)
    {
        var compilation = CSharpCompilation.Create("Fixture", [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
             MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullable));
        var withAnalyzers = compilation.WithAnalyzers([new RedundantNullCheckAnalyzer()]);
        return (await withAnalyzers.GetAnalyzerDiagnosticsAsync()).Where(d => d.Id == "APGEN003").ToList();
    }
}
