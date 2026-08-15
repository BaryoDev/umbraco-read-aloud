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

        // No finally here. A caller that cancels must not evict an attempt the others are still
        // attached to, or the next arrival starts a second synthesis beside the first.
        return await attempt.Value.WaitAsync(ct);
    }

    private async Task<SynthesisResult> SynthesizeAndCacheAsync(SynthesisRequest request, string key)
    {
        try
        {
            // CancellationToken.None on purpose: the work is shared, so one caller walking away must
            // not cancel it for everyone else, and finishing an abandoned synthesis still fills the
            // cache for whoever asks next.
            var result = await _engine.SynthesizeAsync(request, CancellationToken.None);

            // Only a success is written. Caching a failure would poison the key permanently and the
            // next reader would inherit an outage that had long since passed.
            await _cache.SetAsync(key, result, CancellationToken.None);

            return result;
        }
        catch (Exception ex)
        {
            // Logged here rather than at the caller, because this runs once per synthesis however
            // many readers are waiting on it. Logged at the caller, one outage on a popular
            // article becomes hundreds of identical entries and the log stops being readable at
            // the moment it is needed. Rethrown, since every waiter still has to fail.
            _logger.LogWarning(ex, "Read-aloud synthesis failed.");
            throw;
        }
        finally
        {
            // Removed once, when the shared work settles, rather than once per caller. Removing by
            // key alone is safe because this is now the only place an entry is ever removed, so the
            // entry present at this moment can only be this attempt.
            _inFlight.TryRemove(key, out _);
        }
    }
}
