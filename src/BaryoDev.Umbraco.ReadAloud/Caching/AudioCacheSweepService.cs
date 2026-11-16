using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Caching;

/// <summary>
/// Deletes cached audio older than <c>CacheMaxAgeDays</c>, once a day. Does nothing while that
/// option is unset.
/// </summary>
internal sealed class AudioCacheSweepService : BackgroundService
{
    internal const int BatchSize = 500;
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BatchPause = TimeSpan.FromSeconds(1);

    private readonly IAudioCache _cache;
    private readonly IOptionsMonitor<ReadAloudOptions> _options;
    private readonly ILogger<AudioCacheSweepService> _logger;
    private readonly TimeProvider _time;

    public AudioCacheSweepService(
        IAudioCache cache,
        IOptionsMonitor<ReadAloudOptions> options,
        ILogger<AudioCacheSweepService> logger)
        : this(cache, options, logger, TimeProvider.System)
    {
    }

    internal AudioCacheSweepService(
        IAudioCache cache,
        IOptionsMonitor<ReadAloudOptions> options,
        ILogger<AudioCacheSweepService> logger,
        TimeProvider time)
    {
        _cache = cache;
        _options = options;
        _logger = logger;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(BatchPause, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "The read-aloud cache sweep failed. It will try again tomorrow.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One sweep, in batches. Returns how many files were deleted.</summary>
    internal async Task<int> RunOnceAsync(
        TimeSpan pauseBetweenBatches, CancellationToken ct, int batchSize = BatchSize)
    {
        var days = _options.CurrentValue.CacheMaxAgeDays;
        if (days is null) return 0;

        if (_cache is not IAudioCacheSweep sweep)
        {
            _logger.LogInformation(
                "{Section}:CacheMaxAgeDays is set, but the registered audio cache does not support sweeping.",
                ReadAloudOptions.SectionName);
            return 0;
        }

        var cutoff = _time.GetUtcNow().AddDays(-days.Value);
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            var deleted = await sweep.SweepAsync(cutoff, batchSize, ct);
            total += deleted;
            if (deleted < batchSize) break;

            if (pauseBetweenBatches > TimeSpan.Zero) await Task.Delay(pauseBetweenBatches, ct);
        }

        if (total > 0)
        {
            _logger.LogInformation("Read-aloud cache sweep deleted {Count} files older than {Days} days.", total, days);
        }

        return total;
    }
}
