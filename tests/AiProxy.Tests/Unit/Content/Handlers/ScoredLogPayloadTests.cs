using System.Text.Json;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>
/// The log reducer over the scored log payloads: every pre-registered answer survives the reduction.
/// </summary>
public sealed class ScoredLogPayloadTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "ReducerScoring");

    public static TheoryData<string, string> Answers()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "preregistration.json")));
        var data = new TheoryData<string, string>();

        foreach (var payload in document.RootElement.GetProperty("payloads").EnumerateObject())
        {
            if (payload.Value.GetProperty("kind").GetString() != "log")
            {
                continue;
            }

            foreach (var question in payload.Value.GetProperty("questions").EnumerateArray())
            {
                data.Add(payload.Name, question.GetProperty("a").GetString()!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task A_pre_registered_answer_survives_the_reduction(string payload, string answer)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Root, "payloads", payload + ".txt"), TestContext.Current.CancellationToken);

        var result = await new LogCompressionHandler().HandleAsync(
            new ContentPart("request", 1, ContentType.Log, text),
            CancellationToken.None);

        Assert.Contains(answer, Body(result.Text), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tokens_compressed=1419", "tokens_in_context=1031")]
    [InlineData("tokens_compressed=2443", "blocks_compressed=2")]
    public async Task The_answer_stays_on_the_line_of_the_field_that_picks_it(string picked, string answer)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Root, "payloads", "rtk_proxy_log.txt"), TestContext.Current.CancellationToken);

        var result = await new LogCompressionHandler().HandleAsync(
            new ContentPart("request", 1, ContentType.Log, text),
            CancellationToken.None);

        Assert.Contains(Body(result.Text).Split('\n'), line => line.Contains(picked, StringComparison.Ordinal) && line.Contains(answer, StringComparison.Ordinal));
    }

    private static string Body(string reduced)
    {
        var overlay = reduced.IndexOf(MustKeepOverlay.BlockLabel, StringComparison.Ordinal);
        return overlay < 0 ? reduced : reduced[..overlay];
    }
}
