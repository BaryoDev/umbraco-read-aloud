using System.Security.Cryptography;
using System.Text;

namespace BaryoDev.Umbraco.ReadAloud.Engine;

/// <summary>One spoken word and when it is spoken, in milliseconds from the start.</summary>
public sealed record WordBoundary(string Text, double OffsetMs, double DurationMs);

/// <summary>Synthesized audio and the timings that drive word highlighting.</summary>
public sealed record SynthesisResult(
    byte[] Audio,
    IReadOnlyList<WordBoundary> Boundaries,
    string ContentType);

/// <summary>What to say and how to say it.</summary>
public sealed record SynthesisRequest
{
    public required string Text { get; init; }
    public string Voice { get; init; } = "en-US-JennyNeural";
    public string Rate { get; init; } = "+0%";
    public string Pitch { get; init; } = "+0Hz";
    public string Volume { get; init; } = "+0%";
    public bool WordBoundaries { get; init; } = true;

    /// <summary>
    /// A stable identifier for this exact audio, used as the cache file name.
    /// </summary>
    /// <remarks>
    /// Every field that can change the resulting audio is in the hash, separated by NUL characters
    /// which cannot appear in a voice name or prosody value, ensuring field boundaries are unambiguous.
    /// Because the text is included, editing a page changes the key, so the cache invalidates itself
    /// and no stale recording can be served. Hex output keeps it safe as a path segment.
    /// </remarks>
    public string CacheKey() => CacheKey(ReadAloudOptions.EdgeProvider);

    /// <summary>
    /// The same key, for audio made by the named provider.
    /// </summary>
    /// <remarks>
    /// The provider is in the hash because two providers speak the same text differently, with
    /// different audio bytes and different word timings. Without it, switching Edge to Azure would
    /// keep serving the recordings Edge made. The name is lowercased so a hand-typed "azure" and
    /// "Azure" land on one key, matching how the validator compares it. The parameterless overload
    /// is the Edge key.
    /// </remarks>
    public string CacheKey(string provider)
    {
        var material = string.Join('\0', provider.ToLowerInvariant(), Voice, Rate, Pitch, Volume, Text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

/// <summary>
/// The seam between the package and whichever text-to-speech service is configured.
/// </summary>
/// <remarks>
/// Everything above this interface is unaware of which implementation is running, so another
/// service can be put behind it without the controller, the cache or the client changing. This
/// version ships two: the Edge endpoint in this package, and Azure AI Speech in
/// <c>BaryoDev.Umbraco.ReadAloud.Azure</c>. <c>Provider</c> names which one runs, and a value with
/// no engine behind it stops the site at startup rather than falling back.
/// </remarks>
public interface IReadAloudEngine
{
    /// <summary>
    /// The provider name this engine serves, used in the cache key. Null for an engine a site
    /// registered itself, which then caches under the configured <c>Provider</c>.
    /// </summary>
    /// <remarks>
    /// Read from the engine in use rather than from the live option, because the engine is chosen
    /// once at startup. Changing <c>Provider</c> takes a restart, and the cache key follows the
    /// engine so a reload cannot file one provider's audio under another's name.
    /// </remarks>
    string? Provider => null;

    Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default);
}

/// <summary>
/// What a provider package tells the core about itself, so the core can refuse a bad
/// configuration at startup without knowing any provider's settings.
/// </summary>
/// <remarks>
/// The core registers nothing for this. A provider package registers one, and
/// <c>ReadAloudOptions.Provider</c> is accepted only when a registration names it.
/// </remarks>
public interface IReadAloudProviderSettings
{
    /// <summary>The value of <c>Provider</c> this registration answers to.</summary>
    string Provider { get; }

    /// <summary>
    /// The problems with this provider's own settings, each naming the setting. Empty when it is
    /// ready to use. A message must never contain a secret.
    /// </summary>
    IReadOnlyList<string> Validate();
}
