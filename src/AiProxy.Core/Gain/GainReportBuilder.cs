namespace AiProxy.Core.Gain;

/// <summary>Calculates labelled initial-request estimates and UTC calendar views.</summary>
public static class GainReportBuilder
{
    public const decimal CharactersPerToken = 2.89m;

    private static readonly Dimension[] Dimensions =
    [
        new("method", static path => path.Method), new(GainLabels.Dimensions.Rule, static path => path.Rule),
        new("layer", static path => path.Layer), new("pipeline", static path => path.Pipeline)
    ];

    /// <summary>Merges provider and host totals into one row per harness, always in <see cref="GainHarness.Names"/> order.</summary>
    public static IReadOnlyList<GainAttribution> Harnesses(IEnumerable<GainProviderTotals> rows)
    {
        var byHarness = rows.ToLookup(static row => GainHarness.Of(row.Provider, row.Host), static row => row.Totals, StringComparer.Ordinal);
        return GainHarness.Names.Where(byHarness.Contains)
            .Select(name => new GainAttribution(GainHarness.Dimension, name, Sum(byHarness[name])))
            .ToArray();
    }

    public static long Tokens(long characters) => (long)(characters / CharactersPerToken);

    /// <summary>Totals of one request; <paramref name="toolCharacters"/> of the forwarded body are the injected tool definition, which is costed apart from what was saved.</summary>
    public static GainTotals RequestTotals(long originalCharacters, long forwardedCharacters, long originalBytes, long forwardedBytes, long toolCharacters = 0) =>
        new(1, originalCharacters, forwardedCharacters, originalBytes, forwardedBytes,
            Tokens(originalCharacters), Tokens(forwardedCharacters), Tokens(originalCharacters) - Tokens(forwardedCharacters - toolCharacters),
            EstimatedToolTokens: Tokens(toolCharacters), FollowUpMeasuredRequests: 1);

    public static GainReport Build(GainQuery query, DateTimeOffset now, IReadOnlyList<GainHistoryEntry> requests,
        IReadOnlyList<GainFailure> failures, IReadOnlyList<GainRecallEvent> recallEvents, GainOtherTraffic? otherTraffic = null,
        IReadOnlyList<GainFollowUpEvent>? followUps = null)
    {
        var recalls = recallEvents.Select(static recallEvent => recallEvent.Recall).ToArray();
        var summary = Sum(requests.Select(static request => request.Totals));
        summary = summary with
        {
            Recalls = recalls.Length,
            RetrievedCharacters = recalls.Sum(static recall => recall.Characters),
            EstimatedRetrievedTokens = recalls.Sum(static recall => recall.EstimatedTokens)
        };
        followUps ??= [];
        summary = summary with
        {
            FollowUps = followUps.Count,
            FollowUpBytes = followUps.Sum(static followUp => followUp.FollowUp.Bytes),
            EstimatedFollowUpTokens = followUps.Sum(static followUp => followUp.FollowUp.EstimatedTokens)
        };
        var attributed = requests.Select(static request => new Attributed(new GainPath(request.Method, request.Rule, request.Layer, request.Pipeline), request.Totals))
            .Concat(recallEvents.Select(static recallEvent => new Attributed(recallEvent.Path, RecallTotals(recallEvent.Recall))))
            .Concat(followUps.Select(static followUp => new Attributed(followUp.Path, FollowUpTotals(followUp.FollowUp))))
            .ToArray();
        var attributions = Dimensions.SelectMany(dimension => Attribute(dimension.Name, attributed
                .GroupBy(row => dimension.Key(row.Path), StringComparer.Ordinal)
                .Select(group => new GainAttribution(dimension.Name, group.Key, Sum(group.Select(static row => row.Totals))))))
            .ToArray();

        return new GainReport(1, now, query, new GainMeasurement(), summary,
            requests.OrderByDescending(static row => row.At).ThenBy(static row => row.Id, StringComparer.Ordinal).Take(query.HistoryLimit).ToArray(),
            Buckets(requests, Day), Buckets(requests, Week), Buckets(requests, Month), attributions,
            failures.OrderByDescending(static row => row.At).Take(query.HistoryLimit).ToArray(),
            recalls.OrderByDescending(static row => row.At).Take(query.HistoryLimit).ToArray(),
            [
                new GainCapability("quota", false, "No subscription quota model is recorded."),
                new GainCapability("shadow", false, "Shadow executions are not recorded."),
                new GainCapability("hook-command", false, "This proxy records provider traffic, not shell commands."),
                new GainCapability("child-environment", false, "Gain does not launch child commands."),
                new GainCapability("net-billed-savings", false, "Initial-request estimates exclude retransmitted follow-up context and billing effects."),
                new GainCapability("provider-usage", false, "Provider usage is available separately only when optional raw capture is enabled."),
                new GainCapability("project", true, "Only traffic carrying X-Ai-Proxy-Project, or the ai-proxy harness proxy credential, is attributed; absent values remain unknown."),
                new GainCapability("recalls", true, "Caller-attributed retrieval cost; a preview hash does not identify its origin project.")
            ], otherTraffic ?? new GainOtherTraffic());
    }

    /// <summary>One dimension's rows, largest saving first. A rule row naming a comma path counts under each member rule.</summary>
    public static IReadOnlyList<GainAttribution> Attribute(string dimension, IEnumerable<GainAttribution> rows)
    {
        var sums = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var byMember = sums.GetAlternateLookup<ReadOnlySpan<char>>();
        var split = dimension == GainLabels.Dimensions.Rule;
        Span<Range> members = stackalloc Range[16];
        foreach (var row in rows)
        {
            if (!split || !AddMembers(byMember, row.Name, row.Totals, ref members))
            {
                Add(byMember, row.Name, row.Totals);
            }
        }

        var result = new GainAttribution[sums.Count];
        var index = 0;
        foreach (var (name, sum) in sums)
        {
            result[index++] = new GainAttribution(dimension, name, sum.ToTotals());
        }

        Array.Sort(result, static (left, right) => right.Totals.EstimatedSavedTokens.CompareTo(left.Totals.EstimatedSavedTokens) is var order and not 0
            ? order
            : string.CompareOrdinal(left.Name, right.Name));
        return result;
    }

    public static GainTotals Sum(IEnumerable<GainTotals> rows)
    {
        var sum = new Accumulator();
        foreach (var row in rows)
        {
            sum.Add(row);
        }

        return sum.ToTotals();
    }

    /// <summary>Adds <paramref name="totals"/> once under each distinct member of a comma path; false when the path has no member.</summary>
    private static bool AddMembers(Dictionary<string, Accumulator>.AlternateLookup<ReadOnlySpan<char>> byMember, string path, GainTotals totals, ref Span<Range> members)
    {
        var text = path.AsSpan();
        var count = text.Count(',') + 1;
        if (count > members.Length)
        {
            members = new Range[count];
        }

        var distinct = 0;
        foreach (var range in text.Split(','))
        {
            var member = text[range];
            if (member.IsEmpty || Seen(text, members[..distinct], member))
            {
                continue;
            }

            members[distinct++] = range;
            Add(byMember, member, totals);
        }

        return distinct > 0;
    }

    private static bool Seen(ReadOnlySpan<char> text, ReadOnlySpan<Range> members, ReadOnlySpan<char> member)
    {
        foreach (var range in members)
        {
            if (text[range].SequenceEqual(member))
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(Dictionary<string, Accumulator>.AlternateLookup<ReadOnlySpan<char>> byMember, ReadOnlySpan<char> name, GainTotals totals)
    {
        if (!byMember.TryGetValue(name, out var sum))
        {
            sum = new Accumulator();
            byMember[name] = sum;
        }

        sum.Add(totals);
    }

    private static GainTotals RecallTotals(GainRecall recall) => new(Recalls: 1, RetrievedCharacters: recall.Characters, EstimatedRetrievedTokens: recall.EstimatedTokens);

    private static GainTotals FollowUpTotals(GainFollowUp followUp) => new(FollowUps: 1, FollowUpBytes: followUp.Bytes, EstimatedFollowUpTokens: followUp.EstimatedTokens);

    private static GainBucket[] Buckets(IReadOnlyList<GainHistoryEntry> rows, Func<DateTimeOffset, DateTimeOffset> start) =>
        rows.GroupBy(row => start(row.At)).OrderBy(static group => group.Key)
            .Select(static group => new GainBucket(group.Key, Sum(group.Select(static row => row.Totals)))).ToArray();

    private static DateTimeOffset Day(DateTimeOffset at) => new(at.UtcDateTime.Date, TimeSpan.Zero);

    private static DateTimeOffset Month(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Week(DateTimeOffset at)
    {
        var day = Day(at);
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }

    /// <summary>One attribution dimension and how a path names its value in it.</summary>
    private sealed record Dimension(string Name, Func<GainPath, string> Key);

    /// <summary>Totals to attribute, with the path they belong to.</summary>
    private sealed record Attributed(GainPath Path, GainTotals Totals);

    /// <summary>Running totals that add in place, so summing many rows makes one <see cref="GainTotals"/>.</summary>
    private sealed class Accumulator
    {
        private long _requests, _originalCharacters, _forwardedCharacters, _originalBytes, _forwardedBytes, _originalTokens, _forwardedTokens, _savedTokens,
            _recalls, _retrievedCharacters, _retrievedTokens, _toolTokens, _followUps, _followUpBytes, _followUpTokens, _followUpMeasuredRequests;

        public void Add(GainTotals row)
        {
            _requests += row.Requests;
            _originalCharacters += row.OriginalCharacters;
            _forwardedCharacters += row.ForwardedCharacters;
            _originalBytes += row.OriginalBytes;
            _forwardedBytes += row.ForwardedBytes;
            _originalTokens += row.EstimatedOriginalTokens;
            _forwardedTokens += row.EstimatedForwardedTokens;
            _savedTokens += row.EstimatedSavedTokens;
            _recalls += row.Recalls;
            _retrievedCharacters += row.RetrievedCharacters;
            _retrievedTokens += row.EstimatedRetrievedTokens;
            _toolTokens += row.EstimatedToolTokens;
            _followUps += row.FollowUps;
            _followUpBytes += row.FollowUpBytes;
            _followUpTokens += row.EstimatedFollowUpTokens;
            _followUpMeasuredRequests += row.FollowUpMeasuredRequests;
        }

        public GainTotals ToTotals() => new(_requests, _originalCharacters, _forwardedCharacters, _originalBytes, _forwardedBytes, _originalTokens,
            _forwardedTokens, _savedTokens, _recalls, _retrievedCharacters, _retrievedTokens, _toolTokens, _followUps, _followUpBytes, _followUpTokens,
            _followUpMeasuredRequests);
    }
}
