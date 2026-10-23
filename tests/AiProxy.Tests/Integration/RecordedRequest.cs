namespace AiProxy.Tests.Integration;

/// <summary>What a fake upstream received.</summary>
internal sealed record RecordedRequest(
    string Method,
    string Path,
    string QueryString,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    byte[] BodyBytes);
