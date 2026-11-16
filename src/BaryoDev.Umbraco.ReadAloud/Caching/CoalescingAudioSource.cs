using System.Collections.Concurrent;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Caching;

/// <summary>
/// Returns cached audio, synthesizing it once if it is missing however many callers ask at once.
/// </summary>
/// <remarks>
/// Without this, an article shared widely means every reader who presses Listen before the first
/// synthesis finishes opens their own connection to the speech service. That is both slow for them and a
/// good way to run into its limits.
/// </remarks>
public sealed class CoalescingAudioSource : IDisposable
{
    private readonly IReadAloudEngine _engine;
    private readonly IAudioCache _cache;
    private readonly IOptionsMonitor<ReadAloudOptions> _options;
    private readonly ILogger<CoalescingAudioSource> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<SynthesisResult>>> _inFlight = new();

    /// <summary>Cancels every synthesis still running when the application goes away.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>How many synthesis calls are inside the engine right now, across every key.</summary>
    private int _running;

    /// <summary>Most failed keys remembered at once. Past this the oldest entry is dropped.</summary>
    internal const int MaxTrackedFailures = 1000;

    /// <summary>The longest a Retry-After can back every key off.</summary>
    internal const int MaxRetryAfterSeconds = 300;

    private readonly TimeProvider _time;
    private readonly object _backoffGate = new();
    private readonly Dictionary<string, DateTimeOffset> _failedUntil = new();
    private DateTimeOffset _globalUntil = DateTimeOffset.MinValue;

    public CoalescingAudioSource(
        IReadAloudEngine engine,
        IAudioCache cache,
        IOptionsMonitor<ReadAloudOptions> options,
        ILogger<CoalescingAudioSource> logger)
        : this(engine, cache, options, logger, TimeProvider.System)
    {
    }

    internal CoalescingAudioSource(
        IReadAloudEngine engine,
        IAudioCache cache,
        IOptionsMonitor<ReadAloudOptions> options,
        ILogger<CoalescingAudioSource> logger,
        TimeProvider time)
    {
        _engine = engine;
        _cache = cache;
        _options = options;
        _logger = logger;
        _time = time;
    }

    /// <summary>How many keys are remembered as failed right now.</summary>
    internal int TrackedFailures
    {
        get { lock (_backoffGate) return _failedUntil.Count; }
    }

    private bool InBackoff(string key)
    {
        var now = _time.GetUtcNow();
        lock (_backoffGate)
        {
            if (now < _globalUntil) return true;
            if (!_failedUntil.TryGetValue(key, out var until)) return false;
            if (now < until) return true;

            _failedUntil.Remove(key);
            return false;
        }
    }

    private void RecordFailure(string key, Exception ex)
    {
        var options = _options.CurrentValue;
        var now = _time.GetUtcNow();
        var kind = ex switch
        {
            SynthesisRejectedException r => r.Kind,
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                => SynthesisFailureKind.Authentication,
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                => SynthesisFailureKind.RateLimited,
            _ => SynthesisFailureKind.Other,
        };

        TimeSpan global;
        if (kind == SynthesisFailureKind.Authentication)
        {
            global = TimeSpan.FromSeconds(options.AuthFailureBackoffSeconds);
        }
        else if (kind == SynthesisFailureKind.RateLimited)
        {
            var retryAfter = (ex as SynthesisRejectedException)?.RetryAfter;
            global = retryAfter is { } wait && wait > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, MaxRetryAfterSeconds))
                : TimeSpan.FromSeconds(options.FailureBackoffSeconds);
        }
        else
        {
            global = TimeSpan.Zero;
        }

        var perKey = TimeSpan.FromSeconds(options.FailureBackoffSeconds);
        var startedWindow = false;

        lock (_backoffGate)
        {
            if (global > TimeSpan.Zero)
            {
                var until = now + global;
                if (now >= _globalUntil) startedWindow = true;
                if (until > _globalUntil) _globalUntil = until;
            }

            if (perKey > TimeSpan.Zero && kind == SynthesisFailureKind.Other)
            {
                if (!_failedUntil.ContainsKey(key) && _failedUntil.Count >= MaxTrackedFailures)
                {
                    DropExpiredOrOldest(now);
                }

                _failedUntil[key] = now + perKey;
            }
        }

        if (startedWindow)
        {
            _logger.LogWarning(
                "The speech service refused synthesis ({Kind}). Read-aloud skips it for {Seconds} seconds "
                + "and readers get the browser's own voice.",
                kind,
                (int)global.TotalSeconds);
        }
    }

    // Caller holds the gate.
    private void DropExpiredOrOldest(DateTimeOffset now)
    {
        foreach (var expired in _failedUntil.Where(e => e.Value <= now).Select(e => e.Key).ToList())
        {
            _failedUntil.Remove(expired);
        }

        if (_failedUntil.Count < MaxTrackedFailures) return;

        var oldest = _failedUntil.MinBy(e => e.Value).Key;
        _failedUntil.Remove(oldest);
    }

    /// <summary>
    /// False when the engine in use makes no audio (the Browser one). A site that registered its
    /// own engine is true whatever <c>Provider</c> says. Read on every request, because the engine
    /// may follow a setting that changes while the site runs.
    /// </summary>
    internal bool SynthesizesOnServer => _engine.SynthesizesOnServer;

    /// <summary>The key this request is cached under, which the controller derives its ETag from.</summary>
    internal string KeyFor(SynthesisRequest request) =>
        request.CacheKey(_engine.Provider ?? _options.CurrentValue.Provider);

    public async Task<SynthesisResult> GetOrCreateAsync(
        SynthesisRequest request,
        CancellationToken ct = default)
    {
        var key = KeyFor(request);

        var cached = await _cache.GetAsync(key, ct);
        if (cached is not null) return cached;

        // A failure in the last few seconds answers the same way the failure did, without calling
        // the engine. An attempt already running is still joined below.
        if (!_inFlight.ContainsKey(key) && InBackoff(key)) throw new SynthesisBackoffException();

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
        var ceiling = _options.CurrentValue.MaxConcurrentSynthesis;
        var admitted = false;

        try
        {
            // A caller's miss can be older than a synthesis that has since finished and written
            // the cache, so look again before paying for another one.
            var existing = await _cache.GetAsync(key, _shutdown.Token);
            if (existing is not null) return existing;

            // Counted here rather than around the whole method, because this runs exactly once per
            // real synthesis: Lazy guarantees the value factory runs once however many callers race
            // to register it, and a caller that finds an existing entry never arrives here at all.
            admitted = Interlocked.Increment(ref _running) <= ceiling;
            if (!admitted)
            {
                Interlocked.Decrement(ref _running);
                throw new SynthesisBusyException(ceiling);
            }

            // Not the caller's token, and not CancellationToken.None either. The work is shared, so
            // one reader closing a tab must not cancel it for everyone else, and finishing an
            // abandoned synthesis still fills the cache for whoever asks next. But None gives the
            // process no way to stop it at all, and this work holds an open WebSocket and a buffer
            // with the whole recording in it. Linked to shutdown, so the application closing is the
            // one thing that does stop it.
            using var work = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

            var result = await _engine.SynthesizeAsync(request, work.Token);

            // Only a success is written. Caching a failure would poison the key permanently and the
            // next reader would inherit an outage that had long since passed.
            await _cache.SetAsync(key, result, work.Token);

            return result;
        }
        catch (SynthesisBusyException)
        {
            // No stack trace: this is a ceiling doing its job, not a fault. Logged all the same,
            // because an operator seeing it often is being told the site's cold-cache demand has
            // outgrown the ceiling, and the number is theirs to raise.
            _logger.LogWarning(
                "Read-aloud refused a synthesis for cache key {CacheKey}: {Ceiling} are already "
                + "running, which is {Section}:MaxConcurrentSynthesis. The reader gets the browser's "
                + "own voice instead.",
                key,
                ceiling,
                ReadAloudOptions.SectionName);
            throw;
        }
        catch (Exception ex)
        {
            // Logged here rather than at the caller, because this runs once per synthesis however
            // many readers are waiting on it. Logged at the caller, one outage on a popular
            // article becomes hundreds of identical entries and the log stops being readable at
            // the moment it is needed. Rethrown, since every waiter still has to fail.
            //
            // The key names which recording failed, so an operator can tell one article from
            // another. It is a hash of the text and the voice, so it identifies the content and
            // never the reader who asked for it.
            _logger.LogWarning(ex, "Read-aloud synthesis failed for cache key {CacheKey}.", key);
            if (ex is not OperationCanceledException) RecordFailure(key, ex);
            throw;
        }
        finally
        {
            // Only if this attempt took a slot. A refused one already gave its increment back.
            if (admitted) Interlocked.Decrement(ref _running);

            // Removed once, when the shared work settles, rather than once per caller. Removing by
            // key alone is safe because this is now the only place an entry is ever removed, so the
            // entry present at this moment can only be this attempt.
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>Stops every synthesis still running, so none of them outlives the application.</summary>
    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested) return;

        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}

/// <summary>
/// Thrown when as many syntheses are already running as the site allows.
/// </summary>
/// <remarks>
/// Internal on purpose. The controller answers every failure with 503, which the browser client
/// treats as "use the browser's own voice", so a reader gets a working if worse experience and
/// nothing outside this assembly needs to tell this apart from any other failure.
/// </remarks>
internal sealed class SynthesisBusyException : Exception
{
    public SynthesisBusyException(int ceiling)
        : base($"{ceiling} syntheses are already running, which is the configured ceiling.")
    {
    }
}

/// <summary>
/// Thrown instead of calling the engine while a recent failure is still being waited out. Like
/// <see cref="SynthesisBusyException"/> it is internal, and the controller answers it with 503.
/// </summary>
internal sealed class SynthesisBackoffException : Exception
{
    public SynthesisBackoffException()
        : base("A recent synthesis failure is still being backed off.")
    {
    }
}
