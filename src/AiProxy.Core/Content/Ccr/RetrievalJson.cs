namespace AiProxy.Core.Content.Ccr;

/// <summary>The argument names and mode values of a <c>proxy_retrieve</c> call.</summary>
internal static class RetrievalJson
{
    public const string Id = "id";
    public const string Mode = "mode";
    public const string Start = "start";
    public const string End = "end";
    public const string Pattern = "pattern";
    public const string Context = "context";

    public const string LinesMode = "lines";
    public const string PatternMode = "pattern";
    public const string WholeMode = "whole";
}