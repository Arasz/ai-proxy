using System.Collections.Frozen;
using AiProxy.Core.Content.Code;
using TreeSitter;

namespace AiProxy.Code;

/// <summary>How one Tree-sitter grammar's nodes map onto code tokens and removable imports.</summary>
/// <param name="Strings">Literal node types, each with the child type that embeds code inside it, if any.</param>
/// <param name="IsUnsupported">A construct the reducer must not transform, beyond error and missing nodes.</param>
/// <param name="IsImport">A top-level statement that may be removed when it stands alone on its lines.</param>
/// <param name="ImportNeedsTerminator">Whether the code before a removed import must end with a semicolon.</param>
/// <param name="IsFixedName">An identifier whose spelling the markup fixes, such as a JSX tag or attribute name.</param>
/// <param name="IsDirective">A comment, by text and one-based line, that a tool reads and the view must keep.</param>
internal sealed record TreeSitterGrammar(
    FrozenDictionary<string, string?> Strings,
    FrozenSet<string> Comments,
    FrozenSet<string> Identifiers,
    Func<Node, bool> IsFixedName,
    Func<Node, bool> IsUnsupported,
    Func<Node, bool> IsImport,
    bool ImportNeedsTerminator,
    Func<string, string?> ValueText,
    Func<string, int, bool> IsDirective);

/// <summary>Scans a file with a Tree-sitter grammar into tokens covering every character exactly once.</summary>
internal static class TreeSitterScanner
{
    /// <summary>Tokens and removable imports, or unsupported for an unloadable grammar, any error, missing node or unmodelled construct.</summary>
    public static CodeLexResult Lex(string text, TreeSitterLanguage language, TreeSitterGrammar grammar, Func<string, bool> isKeyword)
    {
        if (language.Value is not { } loaded) return Unsupported();
        using var parser = new Parser(loaded);
        using var tree = parser.Parse(text);
        if (tree is null || tree.RootNode.HasError) return Unsupported();
        var spans = Spans(text, tree.RootNode, grammar, isKeyword);
        if (spans is null) return Unsupported();
        var tokens = Cover(text, spans, grammar);
        return tokens is null
            ? Unsupported()
            : new CodeLexResult(true, tokens, Imports(text, tree.RootNode, tokens, grammar));
    }

    private static List<CodeToken>? Spans(string text, Node root, TreeSitterGrammar grammar, Func<string, bool> isKeyword)
    {
        var spans = new List<CodeToken>();
        var stack = new Stack<ScanItem>();
        stack.Push(ScanItem.Visit(root));
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item.Node is not { } node)
            {
                if (item.End > item.Start) spans.Add(new CodeToken(CodeTokenKind.String, text[item.Start..item.End], item.Start, 0));
                continue;
            }

            if (node.IsError || node.IsMissing || grammar.IsUnsupported(node) || node.EndIndex > text.Length) return null;
            var children = node.Children;
            if (node.IsNamed && grammar.Strings.TryGetValue(node.Type, out var embedded))
            {
                PushLiteral(stack, node, children, embedded);
                continue;
            }

            if (children.Count == 0)
            {
                if (node.EndIndex > node.StartIndex) spans.Add(Leaf(text, node, grammar, isKeyword));
                continue;
            }

            for (var index = children.Count - 1; index >= 0; index--) stack.Push(ScanItem.Visit(children[index]));
        }

        return spans;
    }

    private static void PushLiteral(Stack<ScanItem> stack, Node node, IReadOnlyList<Node> children, string? embedded)
    {
        var items = new List<ScanItem>();
        var cursor = node.StartIndex;
        foreach (var child in children)
            if (child.Type == embedded)
            {
                items.Add(ScanItem.Literal(cursor, child.StartIndex));
                items.Add(ScanItem.Visit(child));
                cursor = child.EndIndex;
            }

        items.Add(ScanItem.Literal(cursor, node.EndIndex));
        for (var index = items.Count - 1; index >= 0; index--) stack.Push(items[index]);
    }

    private static CodeToken Leaf(string text, Node node, TreeSitterGrammar grammar, Func<string, bool> isKeyword)
    {
        var spelling = text[node.StartIndex..node.EndIndex];
        if (grammar.Comments.Contains(node.Type)) return new CodeToken(CodeTokenKind.Comment, spelling.TrimEnd('\r'), node.StartIndex, 0);
        if (node.IsNamed && grammar.Identifiers.Contains(node.Type) && !grammar.IsFixedName(node)) return new CodeToken(CodeTokenKind.Identifier, spelling, node.StartIndex, 0, grammar.ValueText(spelling));
        var keyword = node.IsNamed ? isKeyword(spelling) : spelling.All(static character => char.IsAsciiLetter(character) || character == '_');
        return new CodeToken(keyword ? CodeTokenKind.Keyword : CodeTokenKind.Other, spelling, node.StartIndex, 0);
    }

    private static List<CodeToken>? Cover(string text, List<CodeToken> spans, TreeSitterGrammar grammar)
    {
        var tokens = new List<CodeToken>(spans.Count * 2);
        var position = 0;
        foreach (var span in spans)
        {
            if (span.Offset < position) return null;
            AddGap(text, position, span.Offset, tokens);
            tokens.Add(span);
            position = span.Offset + span.Text.Length;
        }

        AddGap(text, position, text.Length, tokens);
        var line = 1;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var kind = token.Kind == CodeTokenKind.Comment && grammar.IsDirective(token.Text, line) ? CodeTokenKind.Other : token.Kind;
            tokens[index] = token with { Kind = kind, Line = line };
            line += LineBreaks(text, tokens[index].Offset, tokens[index].Text.Length);
        }

        return tokens;
    }

    private static void AddGap(string text, int start, int end, List<CodeToken> tokens)
    {
        while (start < end)
        {
            var kind = GapKind(text[start]);
            var stop = start + 1;
            if (kind == CodeTokenKind.NewLine)
                stop += text[start] == '\r' && stop < end && text[stop] == '\n' ? 1 : 0;
            else
                while (stop < end && GapKind(text[stop]) == kind)
                    stop++;

            tokens.Add(new CodeToken(kind, text[start..stop], start, 0));
            start = stop;
        }
    }

    private static CodeTokenKind GapKind(char character)
    {
        return character switch
        {
            ' ' or '\t' or '\f' => CodeTokenKind.Whitespace,
            '\r' or '\n' => CodeTokenKind.NewLine,
            _ => CodeTokenKind.Other
        };
    }

    private static int LineBreaks(string text, int start, int length)
    {
        var breaks = 0;
        for (var index = start; index < start + length; index++)
            if (text[index] == '\r' || (text[index] == '\n' && (index == 0 || text[index - 1] != '\r')))
                breaks++;

        return breaks;
    }

    private static List<CodeSpan> Imports(string text, Node root, List<CodeToken> tokens, TreeSitterGrammar grammar)
    {
        var indexes = new Dictionary<int, int>(tokens.Count);
        for (var index = 0; index < tokens.Count; index++) indexes[tokens[index].Offset] = index;
        var imports = new List<CodeSpan>();
        var removedEnd = -1;
        foreach (var statement in root.Children)
        {
            if (!grammar.IsImport(statement)
                || !indexes.TryGetValue(statement.StartIndex, out var first)
                || (!indexes.TryGetValue(statement.EndIndex, out var after) && statement.EndIndex != text.Length)
                || !StartsLine(tokens, first)
                || !EndsLine(tokens, statement.EndIndex == text.Length ? tokens.Count : after)
                || (grammar.ImportNeedsTerminator && !Terminated(tokens, first, removedEnd)))
                continue;
            imports.Add(new CodeSpan(statement.StartIndex, statement.EndIndex - statement.StartIndex));
            removedEnd = statement.EndIndex;
        }

        return imports;
    }

    private static bool StartsLine(List<CodeToken> tokens, int index)
    {
        for (index--; index >= 0 && tokens[index].Kind != CodeTokenKind.NewLine; index--)
            if (tokens[index].Kind != CodeTokenKind.Whitespace)
                return false;

        return true;
    }

    private static bool EndsLine(List<CodeToken> tokens, int index)
    {
        for (; index < tokens.Count && tokens[index].Kind != CodeTokenKind.NewLine; index++)
            if (tokens[index].Kind is not (CodeTokenKind.Whitespace or CodeTokenKind.Comment))
                return false;

        return true;
    }

    private static bool Terminated(List<CodeToken> tokens, int index, int removedEnd)
    {
        for (index--; index >= 0; index--)
        {
            var token = tokens[index];
            if (token.Kind is not (CodeTokenKind.Whitespace or CodeTokenKind.NewLine or CodeTokenKind.Comment)) return token.Text == ";" || token.Offset + token.Text.Length == removedEnd;
        }

        return true;
    }

    private static CodeLexResult Unsupported()
    {
        return new CodeLexResult(false, []);
    }

    /// <summary>One pending scan step: a node to visit, or the literal text between <c>Start</c> and <c>End</c>.</summary>
    private sealed record ScanItem(Node? Node, int Start, int End)
    {
        public static ScanItem Visit(Node node)
        {
            return new ScanItem(node, 0, 0);
        }

        public static ScanItem Literal(int start, int end)
        {
            return new ScanItem(null, start, end);
        }
    }
}