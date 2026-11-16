using System.Collections.Concurrent;
using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class FailureBackoffTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    private sealed class Engine : IReadAloudEngine
    {
        public int Calls;
        public Exception? Throws;

        public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Throws is not null) throw Throws;
            return Task.FromResult(new SynthesisResult([1], [], "audio/mpeg"));
        }
    }

    private sealed class Cache : IAudioCache
    {
        private readonly ConcurrentDictionary<string, SynthesisResult> _e = new();
        public Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_e.GetValueOrDefault(key));
        public Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default)
        {
            _e[key] = result;
            return Task.CompletedTask;
        }
    }

    private sealed class Opts : IOptionsMonitor<ReadAloudOptions>
    {
        public ReadAloudOptions CurrentValue { get; } = new();
        public ReadAloudOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ReadAloudOptions, string?> listener) => null;
    }

    private static (CoalescingAudioSource Source, Engine Engine, Clock Clock) Build(Exception? throws)
    {
        var engine = new Engine { Throws = throws };
        var clock = new Clock();
        var source = new CoalescingAudioSource(
            engine, new Cache(), new Opts(), NullLogger<CoalescingAudioSource>.Instance, clock);
        return (source, engine, clock);
    }

    private static SynthesisRequest Req(string text = "Hello.") => new() { Text = text };

    private static async Task Fails(CoalescingAudioSource source, string text = "Hello.") =>
        await Should.ThrowAsync<Exception>(async () => await source.GetOrCreateAsync(Req(text)));

    [Fact]
    public async Task A_failed_key_skips_the_engine_inside_the_window_and_calls_it_after()
    {
        var (source, engine, clock) = Build(new InvalidOperationException("down"));

        await Fails(source);
        await Fails(source);
        engine.Calls.ShouldBe(1);

        clock.Advance(31);
        await Fails(source);
        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_plain_failure_does_not_back_off_other_keys()
    {
        var (source, engine, _) = Build(new InvalidOperationException("down"));

        await Fails(source, "one");
        await Fails(source, "two");

        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task An_auth_failure_backs_off_every_key_for_the_auth_window()
    {
        var (source, engine, clock) = Build(
            new SynthesisRejectedException(SynthesisFailureKind.Authentication, "bad key"));

        await Fails(source, "one");
        await Fails(source, "two");
        await Fails(source, "three");
        engine.Calls.ShouldBe(1);

        clock.Advance(299);
        await Fails(source, "two");
        engine.Calls.ShouldBe(1);

        clock.Advance(2);
        await Fails(source, "two");
        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task An_http_401_from_a_custom_engine_counts_as_an_auth_failure()
    {
        var (source, engine, _) = Build(new HttpRequestException("no", null, System.Net.HttpStatusCode.Unauthorized));

        await Fails(source, "one");
        await Fails(source, "two");

        engine.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Retry_After_is_honoured_for_every_key()
    {
        var (source, engine, clock) = Build(
            new SynthesisRejectedException(SynthesisFailureKind.RateLimited, "slow down", TimeSpan.FromSeconds(120)));

        await Fails(source, "one");
        clock.Advance(100);
        await Fails(source, "two");
        engine.Calls.ShouldBe(1);

        clock.Advance(21);
        await Fails(source, "two");
        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Retry_After_is_capped_at_three_hundred_seconds()
    {
        var (source, engine, clock) = Build(
            new SynthesisRejectedException(SynthesisFailureKind.RateLimited, "slow down", TimeSpan.FromHours(2)));

        await Fails(source);
        clock.Advance(301);
        await Fails(source);

        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_429_without_Retry_After_uses_the_failure_window_for_every_key()
    {
        var (source, engine, clock) = Build(
            new SynthesisRejectedException(SynthesisFailureKind.RateLimited, "slow down"));

        await Fails(source, "one");
        await Fails(source, "two");
        engine.Calls.ShouldBe(1);

        clock.Advance(31);
        await Fails(source, "two");
        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task The_failure_map_is_bounded()
    {
        var (source, engine, _) = Build(new InvalidOperationException("down"));

        for (var i = 0; i < CoalescingAudioSource.MaxTrackedFailures + 50; i++)
        {
            await Fails(source, $"text {i}");
        }

        source.TrackedFailures.ShouldBe(CoalescingAudioSource.MaxTrackedFailures);

        // The oldest entry was evicted, so its key reaches the engine again. The newest is still held.
        var before = engine.Calls;
        await Fails(source, "text 0");
        engine.Calls.ShouldBe(before + 1);

        before = engine.Calls;
        await Fails(source, $"text {CoalescingAudioSource.MaxTrackedFailures + 49}");
        engine.Calls.ShouldBe(before);
    }

    [Fact]
    public async Task Expired_entries_are_dropped_before_the_map_evicts_a_live_one()
    {
        var (source, _, clock) = Build(new InvalidOperationException("down"));

        for (var i = 0; i < CoalescingAudioSource.MaxTrackedFailures; i++) await Fails(source, $"old {i}");
        clock.Advance(31);
        await Fails(source, "fresh");

        source.TrackedFailures.ShouldBe(1);
    }

    [Fact]
    public async Task A_cached_key_is_still_served_during_a_global_backoff()
    {
        var (source, engine, _) = Build(null);
        await source.GetOrCreateAsync(Req("cached"));

        engine.Throws = new SynthesisRejectedException(SynthesisFailureKind.Authentication, "bad key");
        await Fails(source, "other");

        (await source.GetOrCreateAsync(Req("cached"))).Audio.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task An_http_403_from_a_custom_engine_counts_as_an_auth_failure()
    {
        var (source, engine, clock) = Build(new HttpRequestException("no", null, System.Net.HttpStatusCode.Forbidden));

        await Fails(source, "one");
        await Fails(source, "two");
        engine.Calls.ShouldBe(1);

        clock.Advance(301);
        await Fails(source, "two");
        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task An_http_429_from_a_custom_engine_backs_off_every_key_for_the_failure_window()
    {
        var (source, engine, clock) = Build(new HttpRequestException("slow", null, System.Net.HttpStatusCode.TooManyRequests));

        await Fails(source, "one");
        await Fails(source, "two");
        engine.Calls.ShouldBe(1);

        clock.Advance(31);
        await Fails(source, "two");
        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Retry_After_is_still_honoured_when_the_failure_window_is_off()
    {
        var engine = new Engine
        {
            Throws = new SynthesisRejectedException(SynthesisFailureKind.RateLimited, "slow", TimeSpan.FromSeconds(60)),
        };
        var options = new Opts();
        options.CurrentValue.FailureBackoffSeconds = 0;
        var source = new CoalescingAudioSource(
            engine, new Cache(), options, NullLogger<CoalescingAudioSource>.Instance, new Clock());

        await Fails(source, "one");
        await Fails(source, "two");

        engine.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ResetBackoff_lets_the_next_request_reach_the_engine()
    {
        var (source, engine, _) = Build(
            new SynthesisRejectedException(SynthesisFailureKind.Authentication, "bad key"));

        await Fails(source, "one");
        await Fails(source, "two");
        engine.Calls.ShouldBe(1);

        source.ResetBackoff();
        await Fails(source, "two");
        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task ResetBackoff_also_forgets_a_per_key_failure()
    {
        var (source, engine, _) = Build(new InvalidOperationException("down"));

        await Fails(source);
        source.ResetBackoff();
        source.TrackedFailures.ShouldBe(0);
        await Fails(source);

        engine.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_timeout_cancellation_with_the_application_running_is_a_plain_failure()
    {
        var (source, engine, _) = Build(new TaskCanceledException("HttpClient timeout"));

        await Fails(source);
        await Fails(source);

        engine.Calls.ShouldBe(1);
        source.TrackedFailures.ShouldBe(1);
    }

    private sealed class HangingEngine : IReadAloudEngine
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.WhenAny(Release.Task, Task.Delay(Timeout.Infinite, ct));
            ct.ThrowIfCancellationRequested();
            return new SynthesisResult([1], [], "audio/mpeg");
        }
    }

    [Fact]
    public async Task Shutdown_cancelling_a_synthesis_is_not_recorded_as_a_failure()
    {
        var engine = new HangingEngine();
        var source = new CoalescingAudioSource(
            engine, new Cache(), new Opts(), NullLogger<CoalescingAudioSource>.Instance, new Clock());

        var call = source.GetOrCreateAsync(Req());
        await engine.Started.Task;
        source.Dispose();

        await Should.ThrowAsync<OperationCanceledException>(async () => await call);
        source.TrackedFailures.ShouldBe(0);
    }

    [Fact]
    public async Task A_caller_that_cancels_records_nothing_and_the_work_still_finishes()
    {
        var engine = new HangingEngine();
        var source = new CoalescingAudioSource(
            engine, new Cache(), new Opts(), NullLogger<CoalescingAudioSource>.Instance, new Clock());

        using var cts = new CancellationTokenSource();
        var call = source.GetOrCreateAsync(Req(), cts.Token);
        await engine.Started.Task;
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await call);

        engine.Release.SetResult();
        (await source.GetOrCreateAsync(Req())).Audio.ShouldNotBeEmpty();
        source.TrackedFailures.ShouldBe(0);
    }

    [Fact]
    public void An_unreasonable_cache_age_stops_startup()
    {
        Validate(new ReadAloudOptions { CacheMaxAgeDays = 36501 }).Failed.ShouldBeTrue();
        Validate(new ReadAloudOptions { CacheMaxAgeDays = int.MaxValue }).Failed.ShouldBeTrue();
        Validate(new ReadAloudOptions { CacheMaxAgeDays = 36500 }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86401)]
    public void Failure_backoff_outside_its_range_stops_startup(int seconds)
    {
        Validate(new ReadAloudOptions { FailureBackoffSeconds = seconds }).Failed.ShouldBeTrue();
        Validate(new ReadAloudOptions { AuthFailureBackoffSeconds = seconds }).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_cache_age_below_one_day_stops_startup(int days)
    {
        Validate(new ReadAloudOptions { CacheMaxAgeDays = days }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Defaults_are_valid_and_the_sweep_is_off()
    {
        var options = new ReadAloudOptions();

        Validate(options).Succeeded.ShouldBeTrue();
        options.CacheMaxAgeDays.ShouldBeNull();
        options.FailureBackoffSeconds.ShouldBe(30);
        options.AuthFailureBackoffSeconds.ShouldBe(300);
        Validate(new ReadAloudOptions { CacheMaxAgeDays = 30, FailureBackoffSeconds = 0 }).Succeeded.ShouldBeTrue();
    }

    private static ValidateOptionsResult Validate(ReadAloudOptions options) =>
        new ReadAloudOptionsValidation([], NullLogger<ReadAloudOptionsValidation>.Instance)
            .Validate(null, options);
}
