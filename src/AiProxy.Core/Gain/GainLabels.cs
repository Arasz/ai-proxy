namespace AiProxy.Core.Gain;

/// <summary>The fixed values the ledger records for attribution, so the recorder and the filters agree.</summary>
public static class GainLabels
{
    /// <summary>Recorded when a value is not known; never inferred.</summary>
    public const string Unknown = "unknown";

    /// <summary>The method a recall made through the API is recorded under.</summary>
    public const string RetrieveOriginal = "retrieve_original";

    /// <summary>The rule of a request the proxy did not change.</summary>
    public static class Rules
    {
        public const string Unchanged = "unchanged";
        public const string CcrPlanFailed = "ccr-plan-failed";

        /// <summary>The rule of a request whose only change is the injected <c>proxy_retrieve</c> tool.</summary>
        public const string CcrTool = "ccr.tool";
    }

    /// <summary>Where in a request the reduction applied.</summary>
    public static class Layers
    {
        public const string ToolResult = "tool_result";
        public const string Retrieval = "retrieval";
    }

    /// <summary>Which pipeline handled the request.</summary>
    public static class Pipelines
    {
        public const string Content = "content";
        public const string Passthrough = "passthrough";
        public const string Ccr = "ccr";
    }

    /// <summary>Who a recall is attributed to.</summary>
    public static class Attributions
    {
        public const string Caller = "caller";
    }
}