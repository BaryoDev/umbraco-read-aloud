using System.Security.Cryptography;
using System.Text;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>The voices of one region. <see cref="Voices"/> is null when the list could not be fetched.</summary>
internal sealed record VoiceListSnapshot(string Region, IReadOnlySet<string>? Voices);

/// <summary>The voice list for the Azure credential in force.</summary>
internal interface IAzureVoiceList
{
    /// <summary>Null when no Azure credential is set. Never throws, apart from the caller's own cancellation.</summary>
    Task<VoiceListSnapshot?> GetAsync(CancellationToken ct);

    /// <summary>Forgets what is kept for this region and key, so the next lookup asks Azure again.</summary>
    void Evict(string region, string key);
}

/// <summary>
/// Fetches the voice list once and keeps it, per region and key. It changes rarely, so a day is
/// enough. A failure is kept for a couple of minutes so an unreachable service is not asked on
/// every dashboard load. One fetch runs at a time.
/// </summary>
/// <remarks>
/// Nothing here can fail startup or a page: any problem becomes a snapshot with no voices. The
/// cache key holds a hash of the key, never the key, and nothing logged names it.
/// </remarks>
internal sealed class AzureVoiceList : IAzureVoiceList
{
    internal static readonly TimeSpan Fresh = TimeSpan.FromHours(24);
    internal static readonly TimeSpan FailedFor = TimeSpan.FromMinutes(2);

    private readonly AzureCredentialResolver _resolver;
    private readonly IAzureVoiceClient _client;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<ReadAloudOptions> _core;
    private readonly SwitchingReadAloudEngine _engine;
    private readonly ILogger<AzureVoiceList> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warned = new();

    public AzureVoiceList(
        AzureCredentialResolver resolver,
        IAzureVoiceClient client,
        IMemoryCache cache,
        IOptionsMonitor<ReadAloudOptions> core,
        SwitchingReadAloudEngine engine,
        ILogger<AzureVoiceList> logger)
    {
        _resolver = resolver;
        _client = client;
        _cache = cache;
        _core = core;
        _engine = engine;
        _logger = logger;
    }

    public async Task<VoiceListSnapshot?> GetAsync(CancellationToken ct)
    {
        try
        {
            return await LookupAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Nothing about the voice list may fail a page or startup.
            return null;
        }
    }

    public void Evict(string region, string key) => _cache.Remove(CacheKey(region, key));

    private async Task<VoiceListSnapshot?> LookupAsync(CancellationToken ct)
    {
        var credential = _resolver.Current;
        if (credential is null) return null;

        var cacheKey = CacheKey(credential.Region, credential.Key);
        if (_cache.TryGetValue(cacheKey, out VoiceListSnapshot? hit) && hit is not null)
        {
            WarnAboutMissingVoices(hit);
            return hit;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(cacheKey, out hit) && hit is not null) return hit;

            // Not tied to the caller's token: one caller leaving must not fail the shared fetch.
            VoiceFetchResult result;
            try
            {
                result = await _client.FetchAsync(credential.Region, credential.Key, CancellationToken.None);
            }
            catch (Exception)
            {
                result = new VoiceFetchResult(VoiceFetchStatus.Network, []);
            }

            if (result.Status == VoiceFetchStatus.Ok)
            {
                var snapshot = new VoiceListSnapshot(credential.Region, result.Voices.ToHashSet(StringComparer.Ordinal));
                _cache.Set(cacheKey, snapshot, Fresh);
                WarnAboutMissingVoices(snapshot);
                return snapshot;
            }

            var failed = new VoiceListSnapshot(credential.Region, null);
            _cache.Set(cacheKey, failed, FailedFor);
            return failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string CacheKey(string region, string key) =>
        "baryodev.readaloud.voices:" + region + ":"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    // Once per process for each region and set of missing voices. Also runs on a cache hit, so a
    // site whose provider became Azure after the list was cached still gets the warning.
    private void WarnAboutMissingVoices(VoiceListSnapshot snapshot)
    {
        try
        {
            if (snapshot.Voices is null) return;
            if (_engine.Active().Provider != ReadAloudOptions.AzureProvider) return;

            var missing = ConfiguredVoices(_core.CurrentValue).Where(v => !snapshot.Voices.Contains(v)).ToList();
            if (missing.Count == 0) return;

            var joined = string.Join(", ", missing);
            if (!_warned.TryAdd(snapshot.Region + "|" + joined, 0)) return;

            _logger.LogWarning(
                "Read Aloud: these configured voices are not available in the Azure region {Region}: {Voices}. "
                + "Readers asking for them will get no audio. Check DefaultVoice and AllowedVoices.",
                snapshot.Region,
                joined);
        }
        catch (Exception)
        {
        }
    }

    internal static IReadOnlyList<string> ConfiguredVoices(ReadAloudOptions options) =>
        new[] { options.DefaultVoice }
            .Concat(options.AllowedVoices ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// Asks for the voice list a few seconds after startup, off the startup path, so a wrong voice is
/// logged without anyone opening the dashboard. Every failure is swallowed.
/// </summary>
internal sealed class AzureVoiceListWarmup : IHostedService
{
    private readonly IAzureVoiceList _voices;
    private readonly CancellationTokenSource _stop = new();

    public AzureVoiceListWarmup(IAzureVoiceList voices) => _voices = voices;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
                await _voices.GetAsync(_stop.Token);
            }
            catch (Exception)
            {
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        return Task.CompletedTask;
    }
}
