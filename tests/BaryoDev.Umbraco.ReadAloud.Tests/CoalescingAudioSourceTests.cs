using System.Collections.Concurrent;
using System.Diagnostics;
using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class CoalescingAudioSourceTests
{
    /// <summary>Counts calls and can be made slow, held open on a gate, or made to fail.</summary>
    private sealed class CountingEngine : IReadAloudEngine
    {
        public int Calls;
        public TimeSpan Delay = TimeSpan.Zero;
        public TaskCompletionSource<bool>? Gate;
        public Exception? Throws;

        /// <summary>The token the last call was handed, so a test can see whether it can cancel.</summary>
        public CancellationToken LastToken;

        public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            LastToken = ct;

            // Waited on through the token, the way a real socket read is, so a test can hold work
            // open and then cancel it rather than only ever release it.
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            else if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Throws is not null) throw Throws;
            return new SynthesisResult([1, 2, 3], [], "audio/mpeg");
        }
    }

    private sealed class MemoryCache : IAudioCache
    {
        private readonly ConcurrentDictionary<string, SynthesisResult> _entries = new();
        private int _writes;
        public int Writes => _writes;

        public Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.GetValueOrDefault(key));

        public Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default)
        {
            _entries[key] = result;
            Interlocked.Increment(ref _writes);
            return Task.CompletedTask;
        }
    }

    private static SynthesisRequest Request() => new() { Text = "Hello world." };

    /// <summary>How long a test waits for work that should already have settled.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>Builds the source under test, with a ceiling high enough to be out of the way.</summary>
    private static CoalescingAudioSource Source(
        IReadAloudEngine engine,
        IAudioCache cache,
        ILogger<CoalescingAudioSource> logger,
        int maxConcurrentSynthesis = 100) =>
        new(engine, cache, Options(maxConcurrentSynthesis), logger);

    private static IOptionsMonitor<ReadAloudOptions> Options(int maxConcurrentSynthesis) =>
        new StaticOptions(new ReadAloudOptions { MaxConcurrentSynthesis = maxConcurrentSynthesis });

    private sealed class StaticOptions : IOptionsMonitor<ReadAloudOptions>
    {
        public StaticOptions(ReadAloudOptions value) => CurrentValue = value;

        public ReadAloudOptions CurrentValue { get; }

        public ReadAloudOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ReadAloudOptions, string?> listener) => null;
    }

    /// <summary>Polls until the condition holds, so tests hold a window open deliberately
    /// instead of hoping a fixed delay outlasts the scheduler.</summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met in time");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_second_request_is_served_from_cache_without_synthesizing_again()
    {
        var engine = new CountingEngine();
        var source = Source(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        await source.GetOrCreateAsync(Request());
        await source.GetOrCreateAsync(Request());

        engine.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Two_hundred_simultaneous_readers_cause_one_synthesis()
    {
        // The scenario this class exists for. A new article shared widely means many readers
        // press Listen at once, and without coalescing that is one WebSocket each. The gate is
        // held open deliberately so all 200 genuinely overlap, rather than hoping a fixed delay
        // outlasts however the scheduler happens to behave.
        var gate = new TaskCompletionSource<bool>();
        var engine = new CountingEngine { Gate = gate };
        var source = Source(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        var tasks = Enumerable.Range(0, 200).Select(_ => source.GetOrCreateAsync(Request())).ToArray();

        await WaitUntil(() => engine.Calls >= 1);
        await Task.Delay(50); // give the other 199 a bounded moment to arrive and queue behind the winner
        gate.SetResult(true);

        await Task.WhenAll(tasks);

        engine.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Two_hundred_simultaneous_readers_cause_one_synthesis_even_when_it_fails()
    {
        // The scenario the semaphore version got wrong. On a failure nothing is cached, so with a
        // gate every waiter woke, found the cache still empty and called the engine itself, turning
        // one outage into two hundred requests against an endpoint that was already unhealthy.
        var gate = new TaskCompletionSource<bool>();
        var engine = new CountingEngine { Gate = gate, Throws = new InvalidOperationException("service down") };
        var source = Source(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        var tasks = Enumerable.Range(0, 200).Select(_ => source.GetOrCreateAsync(Request())).ToArray();

        await WaitUntil(() => engine.Calls >= 1);
        await Task.Delay(50); // give the other 199 a bounded moment to arrive and queue behind the winner
        gate.SetResult(true);

        foreach (var task in tasks)
        {
            await Should.ThrowAsync<InvalidOperationException>(async () => await task);
        }

        engine.Calls.ShouldBe(1);
        tasks.All(t => t.IsFaulted).ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_waiter_does_not_evict_the_attempt_others_are_waiting_on()
    {
        // A reader closing a tab cancels one request. That must not tip the remaining readers into
        // starting a second synthesis, which is what happened when each caller removed the entry in
        // its own finally.
        var gate = new TaskCompletionSource<bool>();
        var engine = new CountingEngine { Gate = gate };
        var source = Source(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        using var cts = new CancellationTokenSource();
        var cancelledCaller = source.GetOrCreateAsync(Request(), cts.Token);
        var otherCaller = source.GetOrCreateAsync(Request());

        await WaitUntil(() => engine.Calls >= 1);

        cts.Cancel();
        var cancelledThrew = false;
        try { await cancelledCaller; }
        catch (OperationCanceledException) { cancelledThrew = true; }
        cancelledThrew.ShouldBeTrue();

        // While the shared work is still held open, a new caller arrives for the same key.
        var lateCaller = source.GetOrCreateAsync(Request());

        gate.SetResult(true);

        var otherResult = await otherCaller;
        var lateResult = await lateCaller;

        engine.Calls.ShouldBe(1);
        lateResult.ShouldBe(otherResult);
    }

    [Fact]
    public async Task A_failure_is_not_cached()
    {
        // Otherwise one outage poisons that article permanently.
        var cache = new MemoryCache();
        var engine = new CountingEngine { Throws = new InvalidOperationException("service down") };
        var source = Source(engine, cache, NullLogger<CoalescingAudioSource>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(async () => await source.GetOrCreateAsync(Request()));

        cache.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task A_failure_does_not_block_the_next_attempt()
    {
        var engine = new CountingEngine { Throws = new InvalidOperationException("transient") };
        var source = Source(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(async () => await source.GetOrCreateAsync(Request()));

        engine.Throws = null;
        (await source.GetOrCreateAsync(Request())).Audio.Length.ShouldBe(3);
        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_failure_is_logged_once_however_many_readers_were_waiting()
    {
        // The design says a failure is logged once, with its cause. Logging it in the controller
        // instead means every waiter logs, so a popular article turns one outage into two hundred
        // identical stack traces and the log stops being readable exactly when it is needed.
        var gate = new TaskCompletionSource<bool>();
        var cause = new InvalidOperationException("service down");
        var engine = new CountingEngine { Gate = gate, Throws = cause };
        var logger = new RecordingLogger<CoalescingAudioSource>();
        var source = Source(engine, new MemoryCache(), logger);

        var tasks = Enumerable.Range(0, 50).Select(_ => source.GetOrCreateAsync(Request())).ToArray();

        await WaitUntil(() => engine.Calls >= 1);
        await Task.Delay(50); // a bounded moment for the other 49 to queue behind the winner
        gate.SetResult(true);

        foreach (var task in tasks)
        {
            await Should.ThrowAsync<InvalidOperationException>(async () => await task);
        }

        logger.Entries.Count.ShouldBe(1);

        var entry = logger.Entries.Single();
        entry.Exception.ShouldBeSameAs(cause, "the cause is the whole point of the entry");

        // Named, so an operator can tell which article failed. The key is a hash over the text and
        // the voice, so it identifies the content and never the reader who asked for it.
        entry.Message.ShouldContain(Request().CacheKey());
    }

    [Fact]
    public async Task A_success_is_not_logged()
    {
        // Otherwise the log grows by a line per article on a healthy site and the failures that
        // matter are buried in it.
        var logger = new RecordingLogger<CoalescingAudioSource>();
        var source = Source(new CountingEngine(), new MemoryCache(), logger);

        await source.GetOrCreateAsync(Request());

        logger.Entries.ShouldBeEmpty();
    }

    /// <summary>Captures what was logged, since the point of the change is how often it happens.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> _entries = new();

        public IReadOnlyCollection<(LogLevel Level, Exception? Exception, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, exception, formatter(state, exception)));
    }

    [Fact]
    public async Task Different_text_does_not_share_a_lock()
    {
        var engine = new CountingEngine();
        var source = Source(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        await source.GetOrCreateAsync(new SynthesisRequest { Text = "One." });
        await source.GetOrCreateAsync(new SynthesisRequest { Text = "Two." });

        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_synthesis_across_different_keys_is_bounded()
    {
        // Coalescing bounds work per key. Nothing bounded the number of keys. Node keys are in the
        // page markup by design, so a caller with the site's own sitemap can ask for a different
        // one every time, abort each request the moment it is sent, and leave a WebSocket and a
        // growing MemoryStream behind for every one of them. The rate limiter counts arrivals, not
        // work still running, and this work outlives the request that started it.
        var gate = new TaskCompletionSource<bool>();
        var engine = new CountingEngine { Gate = gate };
        var source = Source(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance, 2);

        var held = new[] { "One.", "Two." }
            .Select(text => source.GetOrCreateAsync(new SynthesisRequest { Text = text }))
            .ToArray();

        await WaitUntil(() => engine.Calls >= 2);

        var refused = source.GetOrCreateAsync(new SynthesisRequest { Text = "Three." });

        // Through WaitAsync, so a ceiling that stopped working fails this test with a timeout
        // rather than hanging it: without the bound the third call reaches the gated engine and
        // waits there for as long as the test is willing to wait.
        await Should.ThrowAsync<SynthesisBusyException>(async () => await refused.WaitAsync(Patience));
        engine.Calls.ShouldBe(2, "the third key must not reach the engine while two are running");

        gate.SetResult(true);
        await Task.WhenAll(held);
    }

    [Fact]
    public async Task A_finished_synthesis_gives_its_slot_back()
    {
        // The other half. A ceiling that never released would turn the first burst into a
        // permanent outage.
        var engine = new CountingEngine();
        var source = Source(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance, 1);

        await source.GetOrCreateAsync(new SynthesisRequest { Text = "One." });
        await source.GetOrCreateAsync(new SynthesisRequest { Text = "Two." });
        await source.GetOrCreateAsync(new SynthesisRequest { Text = "Three." });

        engine.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task A_refused_synthesis_is_not_cached_and_does_not_block_the_next_attempt()
    {
        // Being busy is a moment, not a property of the article. Caching it, or leaving the key
        // registered as in flight, would turn one busy moment into a permanently silent article.
        var gate = new TaskCompletionSource<bool>();
        var cache = new MemoryCache();
        var engine = new CountingEngine { Gate = gate };
        var source = Source(engine, cache, NullLogger<CoalescingAudioSource>.Instance, 1);

        var held = source.GetOrCreateAsync(new SynthesisRequest { Text = "One." });
        await WaitUntil(() => engine.Calls >= 1);

        await Should.ThrowAsync<SynthesisBusyException>(async () =>
            await source.GetOrCreateAsync(new SynthesisRequest { Text = "Two." }).WaitAsync(Patience));

        gate.SetResult(true);
        await held;

        (await source.GetOrCreateAsync(new SynthesisRequest { Text = "Two." })).Audio.Length.ShouldBe(3);
        cache.Writes.ShouldBe(2, "only the two that ran are written");
    }

    [Fact]
    public async Task Shutdown_cancels_synthesis_that_is_still_running()
    {
        // The shared work deliberately ignores the caller's token, so nothing else can ever stop
        // it. On CancellationToken.None that means a socket to Microsoft and a MemoryStream that
        // outlive not just the request but the application: the host waits on work that has no way
        // to be told the process is going away.
        var gate = new TaskCompletionSource<bool>();
        var engine = new CountingEngine { Gate = gate };
        var source = Source(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        var running = source.GetOrCreateAsync(Request());
        await WaitUntil(() => engine.Calls >= 1);

        engine.LastToken.CanBeCanceled.ShouldBeTrue("CancellationToken.None can never be cancelled");

        source.Dispose();

        await WaitUntil(() => engine.LastToken.IsCancellationRequested);
        await Should.ThrowAsync<OperationCanceledException>(async () => await running.WaitAsync(Patience));
    }
}
