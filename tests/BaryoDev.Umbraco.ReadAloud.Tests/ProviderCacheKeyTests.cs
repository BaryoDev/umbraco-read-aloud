using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class ProviderCacheKeyTests
{
    private static SynthesisRequest Request() => new() { Text = "Hello world." };

    [Fact]
    public void A_different_provider_gives_a_different_key()
    {
        // One provider's recordings, and their timings, must never be served for another.
        Request().CacheKey("Other").ShouldNotBe(Request().CacheKey("Azure"));
    }

    [Fact]
    public void The_provider_is_compared_without_case()
    {
        Request().CacheKey("azure").ShouldBe(Request().CacheKey("Azure"));
    }

    [Fact]
    public void The_azure_key_is_pinned()
    {
        // Audio already cached by an Azure site must keep hitting after an upgrade. If this value
        // changes, every cached recording is orphaned. It is the sha256 of the fields joined with
        // NUL: azure, en-US-JennyNeural, +0%, +0Hz, +0%, Hello world.
        Request().CacheKey("Azure").ShouldBe(
            "FD9B2FCEB0BD8D22E7B0C90062AB1682E95AE2DF945125C80668BBCC1744540A");
    }

    [Fact]
    public async Task The_audio_source_caches_under_the_configured_provider()
    {
        var cache = new RecordingCache();
        var source = new CoalescingAudioSource(
            new StubEngine(),
            cache,
            new Fixed(new ReadAloudOptions { Provider = "Azure" }),
            NullLogger<CoalescingAudioSource>.Instance);

        await source.GetOrCreateAsync(Request());

        cache.Keys.ShouldBe([Request().CacheKey("Azure")]);
        cache.Keys.ShouldNotContain(Request().CacheKey("Other"));
    }

    [Fact]
    public async Task The_key_follows_the_engine_in_use_not_the_live_option()
    {
        // The engine is chosen once at startup. If Provider is reloaded to something else, audio
        // from this engine must still be filed under its own provider.
        var cache = new RecordingCache();
        var source = new CoalescingAudioSource(
            new StubEngine("Azure"),
            cache,
            new Fixed(new ReadAloudOptions { Provider = "Browser" }),
            NullLogger<CoalescingAudioSource>.Instance);

        await source.GetOrCreateAsync(Request());

        cache.Keys.ShouldBe([Request().CacheKey("Azure")]);
    }

    [Fact]
    public void Only_the_browser_engine_is_treated_as_making_no_audio()
    {
        Source(new BrowserVoiceEngine()).SynthesizesOnServer.ShouldBeFalse();
        Source(new StubEngine()).SynthesizesOnServer.ShouldBeTrue();
        Source(new StubEngine("Azure")).SynthesizesOnServer.ShouldBeTrue();
    }

    private static CoalescingAudioSource Source(IReadAloudEngine engine) => new(
        engine,
        new RecordingCache(),
        new Fixed(new ReadAloudOptions()),
        NullLogger<CoalescingAudioSource>.Instance);

    private sealed class StubEngine(string? provider = null) : IReadAloudEngine
    {
        public string? Provider => provider;

        public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SynthesisResult([1, 2, 3], [], "audio/mpeg"));
    }

    private sealed class RecordingCache : IAudioCache
    {
        public List<string> Keys { get; } = [];

        public Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<SynthesisResult?>(null);

        public Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default)
        {
            Keys.Add(key);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixed(ReadAloudOptions value) : IOptionsMonitor<ReadAloudOptions>
    {
        public ReadAloudOptions CurrentValue { get; } = value;

        public ReadAloudOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ReadAloudOptions, string?> listener) => null;
    }

    [Fact]
    public void Word_boundaries_off_gives_a_different_key()
    {
        // A result with no timings must not be served to a request that wants them.
        (Request() with { WordBoundaries = false }).CacheKey("Azure")
            .ShouldNotBe(Request().CacheKey("Azure"));
    }
}
