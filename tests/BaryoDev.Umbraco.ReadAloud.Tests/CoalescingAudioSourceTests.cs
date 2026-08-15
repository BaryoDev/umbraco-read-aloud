using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class CoalescingAudioSourceTests
{
    /// <summary>Counts calls and can be made slow or made to fail.</summary>
    private sealed class CountingEngine : IReadAloudEngine
    {
        public int Calls;
        public TimeSpan Delay = TimeSpan.Zero;
        public Exception? Throws;

        public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Throws is not null) throw Throws;
            return new SynthesisResult([1, 2, 3], [], "audio/mpeg");
        }
    }

    private sealed class MemoryCache : IAudioCache
    {
        private readonly Dictionary<string, SynthesisResult> _entries = new();
        public int Writes;

        public Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.GetValueOrDefault(key));

        public Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default)
        {
            lock (_entries) { _entries[key] = result; Writes++; }
            return Task.CompletedTask;
        }
    }

    private static SynthesisRequest Request() => new() { Text = "Hello world." };

    [Fact]
    public async Task A_second_request_is_served_from_cache_without_synthesizing_again()
    {
        var engine = new CountingEngine();
        var source = new CoalescingAudioSource(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        await source.GetOrCreateAsync(Request());
        await source.GetOrCreateAsync(Request());

        engine.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Two_hundred_simultaneous_readers_cause_one_synthesis()
    {
        // The scenario this class exists for. A new article shared widely means many readers
        // press Listen at once, and without coalescing that is one WebSocket each.
        var engine = new CountingEngine { Delay = TimeSpan.FromMilliseconds(150) };
        var source = new CoalescingAudioSource(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => source.GetOrCreateAsync(Request())));

        engine.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_failure_is_not_cached()
    {
        // Otherwise one outage poisons that article permanently.
        var cache = new MemoryCache();
        var engine = new CountingEngine { Throws = new InvalidOperationException("service down") };
        var source = new CoalescingAudioSource(engine, cache, NullLogger<CoalescingAudioSource>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(async () => await source.GetOrCreateAsync(Request()));

        cache.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task A_failure_does_not_block_the_next_attempt()
    {
        var engine = new CountingEngine { Throws = new InvalidOperationException("transient") };
        var source = new CoalescingAudioSource(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(async () => await source.GetOrCreateAsync(Request()));

        engine.Throws = null;
        (await source.GetOrCreateAsync(Request())).Audio.Length.ShouldBe(3);
        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Different_text_does_not_share_a_lock()
    {
        var engine = new CountingEngine();
        var source = new CoalescingAudioSource(engine, new MemoryCache(), NullLogger<CoalescingAudioSource>.Instance);

        await source.GetOrCreateAsync(new SynthesisRequest { Text = "One." });
        await source.GetOrCreateAsync(new SynthesisRequest { Text = "Two." });

        engine.Calls.ShouldBe(2);
    }
}
