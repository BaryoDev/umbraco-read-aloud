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
    private readonly ConcurrentDictionary<string, Lazy<Task<SynthesisResult>>> _inFlight = new();

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

        // Lazy, because ConcurrentDictionary may run a plain GetOrAdd factory more than once under
        // contention even though only one result is stored. Without it, two callers could each start
        // a synthesis and only one Task would be kept, which is the storm this class exists to avoid.
        var attempt = _inFlight.GetOrAdd(key, k => new Lazy<Task<SynthesisResult>>(
            () => SynthesizeAndCacheAsync(request, k),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // WaitAsync lets one caller give up without cancelling the work the others are waiting on.
            return await attempt.Value.WaitAsync(ct);
        }
        finally
        {
            // Remove only if this is still the same attempt, so a newer one is never evicted.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<SynthesisResult>>>(key, attempt));
        }
    }

    private async Task<SynthesisResult> SynthesizeAndCacheAsync(SynthesisRequest request, string key)
    {
        // CancellationToken.None on purpose: the work is shared, so one caller walking away must not
        // cancel it for everyone else, and finishing an abandoned synthesis still populates the cache.
        var result = await _engine.SynthesizeAsync(request, CancellationToken.None);

        // Only a success is written. Caching a failure would poison the key permanently and the next
        // reader would inherit an outage that had long since passed.
        await _cache.SetAsync(key, result, CancellationToken.None);

        return result;
    }
}
