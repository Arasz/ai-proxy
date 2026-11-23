using AiProxy.Code;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>A source file that gets no code view reports the blank-line path, whatever lexers are loaded.</summary>
public sealed class SourceCodeFallbackPathTests
{
    private static readonly SourceCodeCompressionHandler Handler = new([new CSharpCodeLexer()]);

    [Theory]
    [InlineData("notes.txt", CcrMode.Stateless)]
    [InlineData("Program.cs", CcrMode.Off)]
    [InlineData("Program.cs", CcrMode.Stateless)]
    public async Task A_file_without_a_code_view_reports_the_blank_line_path(string path, CcrMode mode)
    {
        var part = new ContentPart("request", 1, ContentType.SourceCode, "int a = 1;\n\nint b = 2;", path, null, mode);

        var result = await Handler.HandleAsync(part, CancellationToken.None);

        Assert.Equal(SourceCodeCompressionHandler.HandlerPath, result.Path);
        Assert.Equal("int a = 1;\nint b = 2;", result.Text);
    }
}
