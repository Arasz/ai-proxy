namespace AiProxy.Core.Timing;

/// <summary>How long one named step of handling a request took.</summary>
public sealed record StageTiming(string Name, TimeSpan Elapsed);

/// <summary>Collects stage timings in the order stages first ran; a stage that runs again adds to its total.</summary>
public sealed class StageTimings
{
    private readonly List<StageTiming> _stages = [];

    /// <summary>Adds <paramref name="elapsed" /> to the stage called <paramref name="name" />.</summary>
    public void Add(string name, TimeSpan elapsed)
    {
        var index = _stages.FindIndex(stage => string.Equals(stage.Name, name, StringComparison.Ordinal));
        if (index < 0)
        {
            _stages.Add(new StageTiming(name, elapsed));
        }
        else
        {
            _stages[index] = _stages[index] with { Elapsed = _stages[index].Elapsed + elapsed };
        }
    }

    /// <summary>Adds every stage of <paramref name="stages" />, each name prefixed with <paramref name="prefix" />.</summary>
    public void Add(string prefix, IEnumerable<StageTiming> stages)
    {
        foreach (var stage in stages)
        {
            Add($"{prefix}{stage.Name}", stage.Elapsed);
        }
    }

    /// <summary>The stages so far, in the order they first ran.</summary>
    public StageTiming[] ToArray()
    {
        return [.. _stages];
    }
}