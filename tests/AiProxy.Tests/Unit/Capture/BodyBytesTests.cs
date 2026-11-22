using AiProxy.Core.Capture;

namespace AiProxy.Tests.Unit.Capture;

/// <summary>A body held as memory turns back into an array without a copy when it already is one.</summary>
public sealed class BodyBytesTests
{
    [Fact]
    public void Memory_over_a_whole_array_returns_that_array()
    {
        var array = new byte[1024 * 1024];

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = BodyBytes.Array(array);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(array, result);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Memory_over_part_of_an_array_is_copied()
    {
        var array = new byte[] { 1, 2, 3, 4 };

        var result = BodyBytes.Array(array.AsMemory(1, 2));

        Assert.NotSame(array, result);
        Assert.Equal([2, 3], result);
    }

    [Fact]
    public void Memory_over_the_start_of_an_array_is_copied()
    {
        var array = new byte[] { 1, 2, 3, 4 };

        var result = BodyBytes.Array(array.AsMemory(0, 3));

        Assert.NotSame(array, result);
        Assert.Equal([1, 2, 3], result);
    }
}
