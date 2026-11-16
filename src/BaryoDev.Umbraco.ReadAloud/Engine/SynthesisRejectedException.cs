namespace BaryoDev.Umbraco.ReadAloud.Engine;

/// <summary>Why a speech service refused a synthesis, when the engine can tell.</summary>
public enum SynthesisFailureKind
{
    /// <summary>Anything else. Backs off the one cache key.</summary>
    Other,

    /// <summary>The service rejected the credentials (HTTP 401 or 403). Backs off every key.</summary>
    Authentication,

    /// <summary>The service is throttling (HTTP 429). Backs off every key.</summary>
    RateLimited,
}

/// <summary>
/// An engine throws this when the service refused a synthesis in a way that applies to every
/// request, not only the one text. It is optional: any other exception is treated as
/// <see cref="SynthesisFailureKind.Other"/>.
/// </summary>
public sealed class SynthesisRejectedException : InvalidOperationException
{
    public SynthesisRejectedException(SynthesisFailureKind kind, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public SynthesisFailureKind Kind { get; }

    /// <summary>The service's Retry-After, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; }
}
