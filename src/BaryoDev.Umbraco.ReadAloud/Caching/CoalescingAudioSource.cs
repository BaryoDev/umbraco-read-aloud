using System.Collections.Concurrent;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.ReadAloud.Caching;

/// <summary>
/// Returns cached audio, synthesizing it once if it is missing however many callers ask at once.
/// </summary>
/// <remarks>
/// Without this, an article shared widely means every reader who presses Listen before the first
/// synthesis finishes opens their own WebSocket to Microsoft. That is both slow for them and a
/// good way to get an unofficial endpoint closed.
/// </remarks>
public sealed class CoalescingAudioSource
{
    private readonly IReadAloudEngine _engine;
    private readonly IAudioCache _cache;
    private readonly ILogger<CoalescingAudioSource> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public CoalescingAudioSource(
        IReadAloudEngine engine,
        IAudioCache cache,
        ILogger<CoalescingAudioSource> logger)
    {
        _engine = engine;
        _cache = cache;
        _logger = logger;
    }

    public async Task<SynthesisResult> GetOrCreateAsync(
        SynthesisRequest request,
        CancellationToken ct = default)
    {
        var key = request.CacheKey();

        var cached = await _cache.GetAsync(key, ct);
        if (cached is not null) return cached;

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            // Someone may have finished while this caller waited.
            cached = await _cache.GetAsync(key, ct);
            if (cached is not null) return cached;

            var result = await _engine.SynthesizeAsync(request, ct);

            // Only a success is written. Caching a failure would poison the key permanently, and
            // the next reader would inherit an outage that had long since passed.
            await _cache.SetAsync(key, result, ct);

            return result;
        }
        finally
        {
            gate.Release();
            _locks.TryRemove(key, out _);
        }
    }
}
