namespace AiProxy.Core;

/// <summary>The outcome of a check: the input is <see cref="Valid"/>, or <see cref="Invalid"/> with the reason.</summary>
public readonly union Validation(Valid, Invalid);

/// <summary>The input passed the check.</summary>
public sealed record Valid;

/// <summary>The input failed the check, with the reason to show the caller.</summary>
public sealed record Invalid(string Reason);
