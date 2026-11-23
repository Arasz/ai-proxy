namespace AiProxy.Tests;

/// <summary>
///     Tests that measure allocations through <c>ArrayPool&lt;byte&gt;.Shared</c>. They run alone, because a rental on
///     another thread can take the array a warm-up returned and make the measured call allocate.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedArrayPoolCollection
{
    public const string Name = "SharedArrayPool";
}
