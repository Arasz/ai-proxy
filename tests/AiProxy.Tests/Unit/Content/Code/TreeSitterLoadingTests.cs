using AiProxy.Code;
using AiProxy.Configuration;
using AiProxy.Core.Content.Code;
using AiProxy.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class TreeSitterLoadingTests
{
    public static TheoryData<string, string> UnloadableLexers => new()
    {
        { "python", "a.py" },
        { "javascript", "a.js" },
        { "typescript", "a.ts" },
        { "tsx", "a.tsx" }
    };

    [Theory]
    [InlineData("Python", "x = 1\n", "module")]
    [InlineData("JavaScript", "let x = 1;\n", "program")]
    [InlineData("TypeScript", "let x: number = 1;\n", "program")]
    [InlineData("Tsx", "const e = <div>{x}</div>;\n", "program")]
    public void Native_grammar_loads_and_parses_without_errors(string name, string source, string root)
    {
        using var language = new Language(name);
        using var parser = new Parser(language);
        using var tree = parser.Parse(source)!;

        Assert.Equal(root, tree.RootNode.Type);
        Assert.False(tree.RootNode.HasError);
    }

    [Theory]
    [MemberData(nameof(UnloadableLexers))]
    public void A_grammar_that_fails_to_load_makes_the_lexer_unsupported_and_the_reducer_return_the_original(string lexer, string path)
    {
        var missing = new TreeSitterLanguage("NoSuchGrammar", NullLogger<TreeSitterLanguage>.Instance);
        ICodeLexer subject = lexer switch
        {
            "python" => new PythonCodeLexer(missing),
            "javascript" => new JavaScriptCodeLexer(missing),
            _ => new TypeScriptCodeLexer(missing, missing)
        };
        const string source = "// a long removable comment so the compact view plus its hint is smaller than the source\nx = 1\n";

        var lexed = subject.Lex(source, path);
        var reduced = new CodeReducer(subject).Reduce(source, path, null, "[hint]");

        Assert.True(subject.SupportsPath(path));
        Assert.False(lexed.Supported);
        Assert.Empty(lexed.Tokens);
        Assert.False(reduced.Changed);
        Assert.Equal(source, reduced.Text);
        Assert.Null(missing.Value);
    }

    [Fact]
    public void A_grammar_load_failure_is_logged_once()
    {
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var missing = new TreeSitterLanguage("NoSuchGrammar", loggerFactory.CreateLogger<TreeSitterLanguage>());

        Assert.Null(missing.Value);
        Assert.Null(missing.Value);
        var entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1, entry.EventId);
    }

    [Fact]
    public void Compression_registers_each_tree_sitter_language_with_the_standard_logger()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCompression(new CompressionSettings());
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ILogger<TreeSitterLanguage>>());
        Assert.NotNull(provider.GetRequiredKeyedService<TreeSitterLanguage>("Python"));
        Assert.NotNull(provider.GetRequiredKeyedService<TreeSitterLanguage>("JavaScript"));
        Assert.NotNull(provider.GetRequiredKeyedService<TreeSitterLanguage>("TypeScript"));
        Assert.NotNull(provider.GetRequiredKeyedService<TreeSitterLanguage>("Tsx"));
        Assert.Equal(4, provider.GetServices<ICodeLexer>().Count());
    }
}