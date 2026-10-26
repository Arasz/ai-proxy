using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using AiProxy.Ccr;
using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>
/// The proxy_retrieve answer, stated as behaviour: it returns only the slice asked for, from the
/// original it was given, and every failure is a named error rather than a throw.
/// </summary>
public sealed class CcrRetrievalTests
{
    private static readonly CcrOptions Options = CcrOptions.Default;

    private static readonly string Original = string.Join("\n", Enumerable.Range(1, 400).Select(index => $"line {index}: value v{index}"));

    private static readonly string Id = IdOf(Original);

    private static string IdOf(string original) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)))[..16];

    private static CcrRetrieval Answer(string arguments, string? original = null) =>
        Retriever().Answer(arguments, original ?? Original, Options);

    private static CcrRetriever Retriever() => new(new CcrRetrievalParser(new RetrievalArgumentSchema()));

    private static string Lines(int start, int end) => $$"""{"id":"{{Id}}","mode":"lines","start":{{start}},"end":{{end}}}""";

    private static string Pattern(string pattern, int context = 0, string? id = null) =>
        $$"""{"id":"{{id ?? Id}}","mode":"pattern","pattern":{{JsonSerializer.Serialize(pattern)}},"context":{{context}}}""";

    private static string[] Body(CcrRetrieval result) => result.Text.Split('\n')[1..];

    [Fact]
    public void The_definition_is_the_exact_tool_the_adr_declares()
    {
        const string expected = """{"name":"proxy_retrieve","description":"Reads part of a tool result the proxy shortened. Use the id from a [proxy preview ...] line. Ask for lines or a pattern; ask for the whole text only when you need all of it.","input_schema":{"type":"object","properties":{"id":{"type":"string","pattern":"^[0-9a-f]{16}$"},"mode":{"type":"string","enum":["lines","pattern","whole"]},"start":{"type":"integer","minimum":1},"end":{"type":"integer","minimum":1},"pattern":{"type":"string","description":".NET regular expression, case-sensitive"},"context":{"type":"integer","minimum":0,"maximum":10}},"required":["id","mode"],"additionalProperties":false}}""";

        Assert.Equal(expected, CcrRetrieval.Definition);
        Assert.Equal(CcrRetrieval.Name, JsonDocument.Parse(CcrRetrieval.Definition).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Lines_returns_exactly_the_asked_lines_with_their_numbers()
    {
        var result = Answer(Lines(10, 12));

        Assert.Null(result.Error);
        Assert.Equal(["10\tline 10: value v10", "11\tline 11: value v11", "12\tline 12: value v12"], Body(result));
    }

    [Theory]
    [InlineData("lines", ""","start":3,"end":4""")]
    [InlineData("pattern", ""","pattern":"v7$" """)]
    [InlineData("whole", "")]
    public void The_first_line_names_the_id_the_mode_and_the_line_count(string mode, string rest)
    {
        var result = Answer($$"""{"id":"{{Id}}","mode":"{{mode}}"{{rest}}}""");

        Assert.Equal($"[proxy_retrieve {Id} {mode}: 400 lines]", result.Text.Split('\n')[0]);
    }

    [Theory]
    [InlineData(401, 402)]
    [InlineData(12, 10)]
    [InlineData(395, 401)]
    public void A_range_outside_the_text_is_a_bad_range_that_names_the_line_count(int start, int end)
    {
        var result = Answer(Lines(start, end));

        Assert.Equal(CcrRetrieval.BadRange, result.Error);
        Assert.Contains("400 lines", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lines_result_over_the_cap_stops_and_names_the_next_start_line()
    {
        var original = string.Join("\n", Enumerable.Range(1, 400).Select(index => $"{index} {new string('w', 100)}"));

        var result = Retriever().Answer($$"""{"id":"{{IdOf(original)}}","mode":"lines","start":1,"end":400}""", original, Options);

        Assert.Null(result.Error);
        Assert.True(result.Text.Length <= Options.CharactersOf(Options.RetrievalTokens), $"{result.Text.Length} characters is over the cap");
        var body = Body(result);
        var next = body.Length;
        Assert.Equal($"[stopped at the size cap: call again with start {next}]", body[^1]);
        Assert.StartsWith($"{next - 1}\t{next - 1} ", body[^2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_first_requested_line_over_the_cap_says_how_to_retrieve_it()
    {
        var original = new string('x', Options.CharactersOf(Options.RetrievalTokens) + 1);

        var result = Retriever().Answer($$"""{"id":"{{IdOf(original)}}","mode":"lines","start":1,"end":1}""", original, Options);

        Assert.Null(result.Error);
        Assert.Equal(["[line 1 is longer than the size cap: ask for a pattern or the whole text]"], Body(result));
        Assert.True(result.Text.Length <= Options.CharactersOf(Options.RetrievalTokens));
    }

    [Fact]
    public void An_empty_original_has_no_line_range_and_no_pattern_matches()
    {
        var id = IdOf("");

        var range = Retriever().Answer($$"""{"id":"{{id}}","mode":"lines","start":1,"end":1}""", "", Options);
        var pattern = Retriever().Answer(Pattern("value", id: id), "", Options);

        Assert.Equal(CcrRetrieval.BadRange, range.Error);
        Assert.Contains("0 lines", range.Text, StringComparison.Ordinal);
        Assert.Null(pattern.Error);
        Assert.Equal(["[no line matches]"], Body(pattern));
    }

    [Fact]
    public void Pattern_accepts_the_context_limit_and_rejects_one_more()
    {
        var accepted = Answer(Pattern("v10$", Options.MaximumContext));
        var rejected = Answer(Pattern("v10$", Options.MaximumContext + 1));

        Assert.Null(accepted.Error);
        Assert.Equal(20, Body(accepted).Length);
        Assert.Equal("1\tline 1: value v1", Body(accepted)[0]);
        Assert.Equal("20\tline 20: value v20", Body(accepted)[^1]);
        Assert.Equal(CcrRetrieval.BadInput, rejected.Error);
    }

    [Fact]
    public void Pattern_returns_each_matching_line_with_its_context()
    {
        var result = Answer(Pattern(@"v(10|12|30)$", context: 1));

        Assert.Null(result.Error);
        Assert.Equal(
            ["9\tline 9: value v9", "10\tline 10: value v10", "11\tline 11: value v11", "12\tline 12: value v12", "13\tline 13: value v13",
             "--", "29\tline 29: value v29", "30\tline 30: value v30", "31\tline 31: value v31"],
            Body(result));
    }

    [Fact]
    public void Pattern_anchors_match_crlf_lines_without_changing_the_returned_text()
    {
        const string original = "other\r\nexpected value\r\nother\r\n";

        var result = Retriever().Answer(Pattern("^expected value$", id: IdOf(original)), original, Options);

        Assert.Null(result.Error);
        Assert.Equal(["2\texpected value\r"], Body(result));
    }

    [Fact]
    public void Pattern_stops_after_fifty_matches_and_says_so()
    {
        var result = Answer(Pattern("value"));

        var body = Body(result);
        Assert.Equal(51, body.Length);
        Assert.Equal("50\tline 50: value v50", body[49]);
        Assert.Equal("[stopped after 50 matches: narrow the pattern or ask for lines]", body[50]);
    }

    [Fact]
    public void A_pattern_result_over_the_size_cap_stops_and_says_where()
    {
        var original = string.Join("\n", Enumerable.Range(1, 400).Select(index => $"{index} match {new string('w', 200)}"));
        var options = Options with { MaximumMatches = 400 };

        var result = Retriever().Answer(
            $$"""{"id":"{{IdOf(original)}}","mode":"pattern","pattern":"match"}""", original, options);

        Assert.True(result.Text.Length <= options.CharactersOf(options.RetrievalTokens), $"{result.Text.Length} characters is over the cap");
        var body = Body(result);
        var last = body.Length - 1;
        Assert.Equal($"[stopped at the size cap after line {last}: ask for lines from {last + 1} or narrow the pattern]", body[^1]);
    }

    [Fact]
    public void Pattern_on_a_long_line_returns_a_window_around_the_match()
    {
        var original = $"short\n{new string('a', 600)}NEEDLE{new string('b', 600)}\nshort";

        var result = Retriever().Answer(
            $$"""{"id":"{{IdOf(original)}}","mode":"pattern","pattern":"NEEDLE"}""", original, Options);

        var line = Assert.Single(Body(result));
        var window = line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..];
        Assert.StartsWith("2\t[characters ", line, StringComparison.Ordinal);
        Assert.Equal(Options.MatchWindow, window.Length);
        Assert.Contains("NEEDLE", window, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_pattern_window_keeps_complete_unicode_characters_at_both_edges(bool pairAtStart)
    {
        var original = pairAtStart
            ? $"{new string('a', 120)}😀{new string('b', 119)}X{new string('c', 500)}"
            : $"{new string('a', 120)}X{new string('b', 118)}😀{new string('c', 500)}";

        var result = Retriever().Answer(Pattern("X", id: IdOf(original)), original, Options);

        Assert.Null(result.Error);
        var line = Assert.Single(Body(result));
        var expected = pairAtStart
            ? $"1\t[characters 123-361 of 742] {new string('b', 119)}X{new string('c', 119)}"
            : $"1\t[characters 1-239 of 741] {new string('a', 120)}X{new string('b', 118)}";
        Assert.Equal(expected, line);
        Assert.True(new UTF8Encoding(false, true).GetByteCount(result.Text) > 0);
    }

    [Fact]
    public void A_pattern_with_no_match_is_a_normal_empty_result()
    {
        var result = Answer(Pattern("nothing like this"));

        Assert.Null(result.Error);
        Assert.Equal(["[no line matches]"], Body(result));
    }

    [Theory]
    [InlineData("(")]
    [InlineData(@"(a)\1")]
    [InlineData("[z-a]")]
    public void An_invalid_pattern_is_a_bad_pattern(string pattern)
    {
        Assert.Equal(CcrRetrieval.BadPattern, Answer(Pattern(pattern)).Error);
    }

    [Fact]
    public void A_catastrophic_backtracking_pattern_returns_a_normal_answer()
    {
        var original = string.Join("\n", Enumerable.Range(1, 50).Select(_ => $"{new string('a', 40)}!"));
        var arguments = $$"""{"id":"{{IdOf(original)}}","mode":"pattern","pattern":"^(a+)+$"}""";

        var result = Retriever().Answer(arguments, original, Options);

        Assert.Null(result.Error);
        Assert.Equal(["[no line matches]"], Body(result));
    }

    [Fact]
    public void Whole_returns_the_original_byte_for_byte_after_the_first_line()
    {
        var original = "first\r\n\tsecond  \r\nthird\r\n";

        var result = Retriever().Answer($$"""{"id":"{{IdOf(original)}}","mode":"whole"}""", original, Options);

        Assert.Null(result.Error);
        Assert.Equal(original, result.Text[(result.Text.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"mode":"whole"}""")]
    [InlineData("""{"id":"ID","mode":"everything"}""")]
    [InlineData("""{"id":"ID","mode":"whole","extra":1}""")]
    [InlineData("""{"id":"ABCDEF0123456789","mode":"whole"}""")]
    [InlineData("""{"id":"abc","mode":"whole"}""")]
    [InlineData("""{"id":"ID","mode":"lines","start":1}""")]
    [InlineData("""{"id":"ID","mode":"lines","start":"1","end":2}""")]
    [InlineData("""{"id":"ID","mode":"lines","start":1.5,"end":2}""")]
    [InlineData("""{"id":"ID","mode":"pattern"}""")]
    [InlineData("""{"id":"ID","mode":"pattern","pattern":"x","context":11}""")]
    [InlineData("""{"id":"ID","mode":"pattern","pattern":"x","context":-1}""")]
    [InlineData("""{"id":"ID","mode":"lines","start":0,"end":5}""")]
    public void Malformed_arguments_are_bad_input(string arguments)
    {
        Assert.Equal(CcrRetrieval.BadInput, Answer(arguments.Replace("ID", Id, StringComparison.Ordinal)).Error);
    }

    [Theory]
    [InlineData("""{"id":"ID","mode":"pattern","pattern":"\uD800"}""")]
    [InlineData("""{"id":"ID","mode":"pattern","pattern":"\uDC00"}""")]
    [InlineData("""{"id":"\uD800","mode":"whole"}""")]
    [InlineData("""{"id":"ID","mode":"\uD800"}""")]
    [InlineData("""{"id":"ID","mode":"whole","\uD800":1}""")]
    public void Malformed_unicode_arguments_are_bad_input(string arguments)
    {
        Assert.Equal(CcrRetrieval.BadInput, Answer(arguments.Replace("ID", Id, StringComparison.Ordinal)).Error);
    }

    [Theory]
    [InlineData('\uD800')]
    [InlineData('\uDC00')]
    public void A_raw_unpaired_surrogate_in_the_arguments_is_bad_input(char surrogate)
    {
        var arguments = $"{{\"id\":\"{Id}\",\"mode\":\"pattern\",\"pattern\":\"{surrogate}\"}}";

        Assert.Equal(CcrRetrieval.BadInput, Answer(arguments).Error);
    }

    [Fact]
    public void An_id_with_no_original_is_not_kept_and_says_to_rerun_the_tool()
    {
        var result = Retriever().Answer(Lines(1, 2), null, Options);

        Assert.Equal(CcrRetrieval.NotKept, result.Error);
        Assert.Contains("Re-run the tool", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_original_that_does_not_match_the_id_is_not_kept()
    {
        var result = Answer(Lines(1, 2), "some other text");

        Assert.Equal(CcrRetrieval.NotKept, result.Error);
    }

    [Fact]
    public void A_schema_violation_is_bad_input_carrying_the_schema_reason()
    {
        var retriever = new CcrRetriever(new CcrRetrievalParser(new RejectingSchema("start must be at least 1")));

        var result = retriever.Answer(Lines(1, 2), Original, Options);

        Assert.Equal(CcrRetrieval.BadInput, result.Error);
        Assert.Contains("start must be at least 1", result.Text, StringComparison.Ordinal);
    }

    private sealed class RejectingSchema(string violation) : IToolArgumentSchema
    {
        public string? Violation(JsonElement arguments) => violation;
    }
}
