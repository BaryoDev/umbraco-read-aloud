using BaryoDev.Umbraco.ReadAloud.Engine;

namespace BaryoDev.Umbraco.ReadAloud.Caching;

/// <summary>Stores synthesized audio so the same text is never paid for twice.</summary>
public interface IAudioCache
{
    Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default);
}
