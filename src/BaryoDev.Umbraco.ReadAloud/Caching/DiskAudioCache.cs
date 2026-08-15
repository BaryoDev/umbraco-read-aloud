using System.Text.Json;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.ReadAloud.Caching;

/// <summary>
/// Caches audio as a pair of files per key: the MP3 and its word timings.
/// </summary>
/// <remarks>
/// Disk rather than the database, because these are binary blobs that would bloat a backup and
/// which SQLite in particular handles poorly. Everything here is derived data: deleting the
/// folder is always safe and the next request regenerates what is needed.
/// </remarks>
public sealed class DiskAudioCache : IAudioCache
{
    private readonly string _root;
    private readonly ILogger<DiskAudioCache> _logger;

    public DiskAudioCache(string rootPath, ILogger<DiskAudioCache> logger)
    {
        _root = rootPath;
        _logger = logger;
    }

    public async Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default)
    {
        var (audioPath, timingsPath) = Paths(key);

        // Both halves or neither. A crash between the two writes leaves audio with no timings,
        // and serving that gives a reader audio with silently broken highlighting.
        if (!File.Exists(audioPath) || !File.Exists(timingsPath)) return null;

        try
        {
            var audio = await File.ReadAllBytesAsync(audioPath, ct);
            var json = await File.ReadAllTextAsync(timingsPath, ct);
            var boundaries = JsonSerializer.Deserialize<List<WordBoundary>>(json) ?? [];

            return new SynthesisResult(audio, boundaries, "audio/mpeg");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Discarding an unreadable read-aloud cache entry.");
            return null;
        }
    }

    public async Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default)
    {
        var (audioPath, timingsPath) = Paths(key);

        Directory.CreateDirectory(_root);

        // Both files must exist for a read to succeed, so a crash between the two writes leaves
        // an entry that is correctly treated as a miss regardless of order.
        await File.WriteAllTextAsync(timingsPath, JsonSerializer.Serialize(result.Boundaries), ct);
        await File.WriteAllBytesAsync(audioPath, result.Audio, ct);
    }

    private (string Audio, string Timings) Paths(string key)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A cache key must be 64 hex characters.", nameof(key));
        }

        return (Path.Combine(_root, $"{key}.mp3"), Path.Combine(_root, $"{key}.json"));
    }
}
