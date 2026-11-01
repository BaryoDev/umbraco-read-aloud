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
        // Switching Edge to Azure must not serve the recordings, or the timings, Edge made.
        Request().CacheKey("Edge").ShouldNotBe(Request().CacheKey("Azure"));
    }

    [Fact]
    public void The_provider_is_compared_without_case()
    {
        Request().CacheKey("azure").ShouldBe(Request().CacheKey("Azure"));
    }

    [Fact]
    public void The_key_without_a_provider_is_the_edge_key()
    {
        Request().CacheKey().ShouldBe(Request().CacheKey("Edge"));
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
        cache.Keys.ShouldNotContain(Request().CacheKey("Edge"));
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
            new Fixed(new ReadAloudOptions { Provider = "Edge" }),
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
}
