using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content;

/// <summary>The routing table, stated as behaviour: every kind has exactly one handler.</summary>
public sealed class ContentRoutingTableTests
{
    private static readonly ICompressionHandler[] Handlers =
    [
        new LogCompressionHandler(),
        new JsonCompressionHandler(),
        new XmlCompressionHandler(),
        new TomlCompressionHandler(),
        new MarkdownCompressionHandler(),
        new SourceCodeCompressionHandler([]),
        new ProseCompressionHandler(),
        new DirectoryListingCompressionHandler(),
        new ShellTranscriptCompressionHandler(),
        new BuildOutputCompressionHandler(),
        new PassThroughCompressionHandler()
    ];

    [Fact]
    public void Every_kind_is_claimed_by_exactly_one_handler()
    {
        foreach (var kind in Enum.GetValues<ContentType>())
        {
            var claiming = Handlers.Where(handler => handler.CanHandle(kind)).ToArray();

            Assert.Single(claiming);
        }
    }

    [Fact]
    public void The_handler_that_claims_a_kind_is_the_one_the_table_names()
    {
        var expected = new Dictionary<ContentType, Type>
        {
            [ContentType.Log] = typeof(LogCompressionHandler),
            [ContentType.Json] = typeof(JsonCompressionHandler),
            [ContentType.Xml] = typeof(XmlCompressionHandler),
            [ContentType.Toml] = typeof(TomlCompressionHandler),
            [ContentType.Markdown] = typeof(MarkdownCompressionHandler),
            [ContentType.SourceCode] = typeof(SourceCodeCompressionHandler),
            [ContentType.Prose] = typeof(ProseCompressionHandler),
            [ContentType.DirectoryListing] = typeof(DirectoryListingCompressionHandler),
            [ContentType.ShellTranscript] = typeof(ShellTranscriptCompressionHandler),
            [ContentType.BuildOutput] = typeof(BuildOutputCompressionHandler),
            [ContentType.Unknown] = typeof(PassThroughCompressionHandler)
        };

        var router = new ContentRouter(Handlers, new PassThroughCompressionHandler());

        foreach (var (kind, handler) in expected)
        {
            var route = router.Route(new ContentPart("request", 1, kind, "text"));

            Assert.IsType(handler, route.Handler);
        }
    }

    [Fact]
    public async Task Every_reducer_keeps_a_subset_of_the_text_it_was_given()
    {
        // The promise the whole design rests on: a reducer never invents a word. The
        // document-shaped reducers are checked by their values instead, because the
        // whitespace they remove is not a line of the input.
        var text = string.Join("\n", "2026-03-12 10:00:00 INFO worker=api-1 request started", "2026-03-12 10:00:01 INFO worker=api-1 request failed status=503", "2026-03-12 10:00:02 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:03 INFO worker=api-1 nothing to see", "", "key = \"value\"", "# a comment", "", "# Heading", "", "    return null;", "", "$ echo done");

        var kinds = new (ContentType Kind, ICompressionHandler Handler)[]
        {
            (ContentType.Log, new LogCompressionHandler()),
            (ContentType.Toml, new TomlCompressionHandler()),
            (ContentType.Markdown, new MarkdownCompressionHandler()),
            (ContentType.SourceCode, new SourceCodeCompressionHandler([])),
            (ContentType.Prose, new ProseCompressionHandler()),
            (ContentType.ShellTranscript, new ShellTranscriptCompressionHandler())
        };

        foreach (var (kind, handler) in kinds)
        {
            var result = await handler.HandleAsync(new ContentPart("request", 1, kind, text), CancellationToken.None);
            var input = text.Split('\n');

            var kept = result.Text
                .Split('\n')
                .Where(line => line.Length > 0 && !line.StartsWith("[retained identifiers]", StringComparison.Ordinal))
                .ToArray();

            // Each kept line is an input line, and the kept lines follow the input's order: a
            // reduction drops, it does not shuffle. The log reducer may also shorten a line to
            // some of its words, in their order.
            var position = 0;
            foreach (var line in kept)
            {
                var found = Array.FindIndex(input, position, candidate => kind == ContentType.Log
                    ? WordsOf(line, candidate)
                    : string.Equals(candidate, line, StringComparison.Ordinal));
                Assert.True(found >= 0, $"the line '{line}' is not in the input after the line before it");
                position = found + 1;
            }
        }
    }

    private static bool WordsOf(string kept, string line)
    {
        var words = line.Split(' ');
        var position = 0;
        foreach (var word in kept.Split(' '))
        {
            position = Array.IndexOf(words, word, position);
            if (position < 0)
            {
                return false;
            }

            position++;
        }

        return true;
    }
}