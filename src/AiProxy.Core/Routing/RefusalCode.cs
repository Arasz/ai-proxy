namespace AiProxy.Core.Routing;

/// <summary>The wire code of each refusal, in one place so the code and the reason cannot drift apart.</summary>
public static class RefusalCode
{
    /// <summary>The code of a refusal whose provider was never named.</summary>
    public const string Unknown = "routing_unknown";

    /// <summary>The code of a refusal whose credential was not established.</summary>
    public const string CredentialAmbiguous = "routing_credential_ambiguous";

    /// <summary>The code that goes on the wire for a refusal.</summary>
    public static string Of(RefusalReason reason) => reason switch
    {
        RefusalReason.Unknown => Unknown,
        RefusalReason.CredentialAmbiguous => CredentialAmbiguous,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown refusal reason."),
    };
}
