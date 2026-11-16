using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class CacheSweepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ra-sweep-" + Guid.NewGuid().ToString("N"));
    private readonly DiskAudioCache _cache;

    public CacheSweepTests()
    {
        Directory.CreateDirectory(_dir);
        _cache = new DiskAudioCache(_dir, NullLogger<DiskAudioCache>.Instance);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Key(char c) => new(c, 64);

    private string Touch(string name, int daysOld)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public async Task Sweep_deletes_only_old_cache_files()
    {
        var oldMp3 = Touch(Key('a') + ".mp3", 40);
        var oldJson = Touch(Key('a') + ".json", 40);
        var freshMp3 = Touch(Key('b') + ".mp3", 1);
        var freshJson = Touch(Key('b') + ".json", 1);

        var deleted = await _cache.SweepAsync(DateTimeOffset.UtcNow.AddDays(-30), 100);

        deleted.ShouldBe(2);
        File.Exists(oldMp3).ShouldBeFalse();
        File.Exists(oldJson).ShouldBeFalse();
        File.Exists(freshMp3).ShouldBeTrue();
        File.Exists(freshJson).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_leaves_files_that_are_not_cache_entries()
    {
        var notes = Touch("notes.txt", 400);
        var shortName = Touch("abc.mp3", 400);
        var temp = Touch(Key('c') + ".mp3.0123.tmp", 400);

        (await _cache.SweepAsync(DateTimeOffset.UtcNow.AddDays(-30), 100)).ShouldBe(0);

        File.Exists(notes).ShouldBeTrue();
        File.Exists(shortName).ShouldBeTrue();
        File.Exists(temp).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_stops_at_the_batch_size()
    {
        for (var i = 0; i < 5; i++) Touch(new string((char)('a' + i), 64) + ".mp3", 40);

        var deleted = await _cache.SweepAsync(DateTimeOffset.UtcNow.AddDays(-30), 3);

        deleted.ShouldBe(3);
        Directory.GetFiles(_dir).Length.ShouldBe(2);
    }

    [Fact]
    public async Task Sweep_of_a_missing_folder_deletes_nothing()
    {
        var cache = new DiskAudioCache(Path.Combine(_dir, "nope"), NullLogger<DiskAudioCache>.Instance);

        (await cache.SweepAsync(DateTimeOffset.UtcNow, 10)).ShouldBe(0);
    }

    private sealed class Opts(int? days) : IOptionsMonitor<ReadAloudOptions>
    {
        public ReadAloudOptions CurrentValue { get; } = new() { CacheMaxAgeDays = days };
        public ReadAloudOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ReadAloudOptions, string?> listener) => null;
    }

    private sealed class CountingSweep : IAudioCache, IAudioCacheSweep
    {
        public int Sweeps;
        public DateTimeOffset? Cutoff;
        public Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<SynthesisResult?>(null);
        public Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> SweepAsync(DateTimeOffset olderThan, int maxFiles, CancellationToken ct = default)
        {
            Sweeps++;
            Cutoff = olderThan;
            return Task.FromResult(0);
        }
    }

    [Fact]
    public async Task The_job_does_nothing_when_the_option_is_off()
    {
        var cache = new CountingSweep();
        var job = new AudioCacheSweepService(cache, new Opts(null), NullLogger<AudioCacheSweepService>.Instance);

        (await job.RunOnceAsync(TimeSpan.Zero, default)).ShouldBe(0);

        cache.Sweeps.ShouldBe(0);
    }

    [Fact]
    public async Task The_job_sweeps_with_a_cutoff_the_configured_days_back()
    {
        var cache = new CountingSweep();
        var job = new AudioCacheSweepService(cache, new Opts(30), NullLogger<AudioCacheSweepService>.Instance);

        await job.RunOnceAsync(TimeSpan.Zero, default);

        cache.Sweeps.ShouldBe(1);
        cache.Cutoff!.Value.ShouldBe(DateTimeOffset.UtcNow.AddDays(-30), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task The_job_skips_a_cache_that_cannot_sweep()
    {
        var job = new AudioCacheSweepService(new NoSweepCache(), new Opts(30), NullLogger<AudioCacheSweepService>.Instance);

        (await job.RunOnceAsync(TimeSpan.Zero, default)).ShouldBe(0);
    }

    private sealed class NoSweepCache : IAudioCache
    {
        public Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<SynthesisResult?>(null);
        public Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default) => Task.CompletedTask;
    }
}
