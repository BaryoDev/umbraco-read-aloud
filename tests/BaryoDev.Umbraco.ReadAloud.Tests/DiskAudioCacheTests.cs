using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class DiskAudioCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"readaloud-tests-{Guid.NewGuid():N}");

    private DiskAudioCache Cache() => new(_root, NullLogger<DiskAudioCache>.Instance);

    private static SynthesisResult Result() => new(
        [0xFF, 0xFB, 0x90, 0x64],
        [new WordBoundary("Hello", 100, 500)],
        "audio/mpeg");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_miss_returns_null_rather_than_throwing()
    {
        (await Cache().GetAsync("A".PadLeft(64, 'A'))).ShouldBeNull();
    }

    [Fact]
    public async Task What_goes_in_comes_back_out()
    {
        var key = "B".PadLeft(64, 'B');
        var cache = Cache();

        await cache.SetAsync(key, Result());
        var found = await cache.GetAsync(key);

        found.ShouldNotBeNull();
        found.Audio.ShouldBe(Result().Audio);
        found.ContentType.ShouldBe("audio/mpeg");
        found.Boundaries.Count.ShouldBe(1);
        found.Boundaries[0].Text.ShouldBe("Hello");
        found.Boundaries[0].OffsetMs.ShouldBe(100);
    }

    [Fact]
    public async Task A_key_that_is_not_hex_is_refused()
    {
        // The key becomes a file name. Anything but hex is either a bug or a traversal attempt.
        var cache = Cache();

        await Should.ThrowAsync<ArgumentException>(async () =>
            await cache.GetAsync("../../../etc/passwd"));
        await Should.ThrowAsync<ArgumentException>(async () =>
            await cache.SetAsync("../../../etc/passwd", Result()));
    }

    [Fact]
    public async Task A_half_written_entry_is_treated_as_a_miss()
    {
        // Audio written but timings missing, which is what a crash mid-write leaves behind.
        // Serving that would give audio with no highlighting and no way to notice.
        var key = "C".PadLeft(64, 'C');
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Path.Combine(_root, $"{key}.mp3"), [1, 2, 3]);

        (await Cache().GetAsync(key)).ShouldBeNull();
    }

    [Fact]
    public async Task An_unreadable_cache_file_is_treated_as_a_miss_not_an_exception()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // File.SetUnixFileMode has no Windows equivalent to force here, so there is
            // nothing this test can safely simulate on that platform.
            return;
        }

        var key = "D".PadLeft(64, 'D');
        var cache = Cache();
        await cache.SetAsync(key, Result());
        var audioPath = Path.Combine(_root, $"{key}.mp3");

        File.SetUnixFileMode(audioPath, UnixFileMode.None);
        try
        {
            // Root, and some CI runners, ignore Unix permissions entirely. Confirm this
            // environment actually enforces them before asserting, or the test would pass
            // for the wrong reason.
            bool permissionsEnforced;
            try
            {
                await File.ReadAllBytesAsync(audioPath);
                permissionsEnforced = false;
            }
            catch (UnauthorizedAccessException)
            {
                permissionsEnforced = true;
            }

            if (!permissionsEnforced) return;

            (await Cache().GetAsync(key)).ShouldBeNull();
        }
        finally
        {
            File.SetUnixFileMode(audioPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task A_successful_write_leaves_only_the_two_expected_files()
    {
        var key = "E".PadLeft(64, 'E');
        var cache = Cache();

        await cache.SetAsync(key, Result());

        Directory.GetFiles(_root, "*.tmp").ShouldBeEmpty();
        Directory.GetFiles(_root).Length.ShouldBe(2);
    }

    [Fact]
    public async Task A_write_interrupted_by_cancellation_leaves_no_temp_files_or_entry()
    {
        var key = "F".PadLeft(64, 'F');
        var cache = Cache();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await cache.SetAsync(key, Result(), cts.Token));

        if (Directory.Exists(_root))
        {
            Directory.GetFiles(_root, $"{key}.*").ShouldBeEmpty();
        }

        (await Cache().GetAsync(key)).ShouldBeNull();
    }
}
