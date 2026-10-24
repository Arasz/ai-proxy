using AiProxy.Core.Compression;
using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Compression;

/// <summary>The pipeline that changes nothing, which is the state of the question today.</summary>
public sealed class PassThroughCompressionPipelineTests
{
    private readonly PassThroughCompressionPipeline _pipeline = new();

    [Fact]
    public async Task The_body_comes_back_byte_for_byte()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"messages\":[{\"role\":\"user\",\"content\":\"héllo\"}]}");

        var result = await _pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.Anthropic),
            CancellationToken.None);

        Assert.Equal(body, result.Body.ToArray());
    }

    [Fact]
    public async Task The_pipeline_says_it_changed_nothing()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"model\":\"gpt-4o\"}");

        var result = await _pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.False(result.Changed);
    }

    [Fact]
    public async Task An_empty_body_stays_empty()
    {
        var result = await _pipeline.CompressAsync(
            new CompressionRequest(ReadOnlyMemory<byte>.Empty, null, Dialect.OpenAi),
            CancellationToken.None);

        Assert.Empty(result.Body.ToArray());
    }
}
