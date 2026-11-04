namespace AiProxy.Core.Doctor;

/// <summary>The names in the proxy's <c>/health</c> answer, shared by the endpoint and the doctor.</summary>
public static class HealthApi
{
    /// <summary>The endpoint path.</summary>
    public const string Path = "/health";

    /// <summary>The product name the proxy reports.</summary>
    public const string Product = "ai-proxy";
}
