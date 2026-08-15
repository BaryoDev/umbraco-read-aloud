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
    /// Every field that can change the resulting audio is in the hash, separated so they cannot
    /// run together and cause collisions. Because the text is included, editing a page changes
    /// the key, so the cache invalidates itself and no stale recording can be served. Hex output
    /// keeps it safe as a path segment.
    /// </remarks>
    public string CacheKey()
    {
        var material = string.Join(' ', Voice, Rate, Pitch, Volume, Text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

/// <summary>
/// The seam between the package and whichever text-to-speech service is configured.
/// </summary>
/// <remarks>
/// Everything above this interface is unaware of which implementation is running, which is what
/// lets a site swap the free Edge endpoint for Azure Speech with a config change when it needs a
/// contract rather than a favour.
/// </remarks>
public interface IReadAloudEngine
{
    Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default);
}
