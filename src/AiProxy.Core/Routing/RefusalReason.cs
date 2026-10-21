namespace AiProxy.Core.Routing;

/// <summary>Why a request was refused rather than relayed.</summary>
public enum RefusalReason
{
    /// <summary>No provider was named and no pass-through rule matched.</summary>
    Unknown,

    /// <summary>The provider was named, but the credential to use was not established.</summary>
    CredentialAmbiguous,
}
