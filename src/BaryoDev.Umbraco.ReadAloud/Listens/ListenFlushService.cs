using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.ReadAloud.Listens;

/// <summary>
/// Writes the in-memory counts to the database every minute, and once more at shutdown.
/// </summary>
/// <remarks>
/// Waits for Umbraco to reach <see cref="RuntimeLevel.Run"/> first, so it never writes during an
/// install or an upgrade. A failed flush puts its counts back in memory and tries again on the next
/// tick, and logs once per failure streak. A missing table counts as a failure like any other,
/// which is what a site sees until the migration has run.
/// </remarks>
internal sealed class ListenFlushService : BackgroundService
{
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan ReadyPoll = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly ListenCounter _counter;
    private readonly IListenStore _store;
    private readonly Func<bool> _ready;
    private readonly IOptionsMonitor<ReadAloudOptions> _options;
    private readonly ILogger<ListenFlushService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _readyPoll;
    private readonly TimeSpan _shutdownTimeout;
    private int _failing;

    public ListenFlushService(
        ListenCounter counter,
        IListenStore store,
        IRuntimeState runtime,
        IOptionsMonitor<ReadAloudOptions> options,
        ILogger<ListenFlushService> logger)
        : this(counter, store, () => runtime.Level == RuntimeLevel.Run, options, logger,
            FlushInterval, ReadyPoll, ShutdownTimeout)
    {
    }

    internal ListenFlushService(
        ListenCounter counter,
        IListenStore store,
        Func<bool> ready,
        IOptionsMonitor<ReadAloudOptions> options,
        ILogger<ListenFlushService> logger,
        TimeSpan interval,
        TimeSpan readyPoll,
        TimeSpan shutdownTimeout)
    {
        _counter = counter;
        _store = store;
        _ready = ready;
        _options = options;
        _logger = logger;
        _interval = interval;
        _readyPoll = readyPoll;
        _shutdownTimeout = shutdownTimeout;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!_ready()) await Task.Delay(_readyPoll, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_interval, stoppingToken);
                try
                {
                    await Task.Run(() => Flush(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Reading the options can throw after a bad reload. Keep the loop and the held counts.
                    _logger.LogWarning(ex, "Read-aloud listen counts could not be flushed. The next flush will retry.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Flushes once more on the way out. Never throws, and never waits past the timeout.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // One deadline covers both waits, so a stuck periodic flush leaves the final one only the time left.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_shutdownTimeout);

        try
        {
            await base.StopAsync(deadline.Token);
        }
        catch (Exception)
        {
        }

        try
        {
            if (!_ready()) return;

            if (deadline.IsCancellationRequested)
            {
                DropAtShutdown();
                return;
            }

            var written = await Task.Run(() => Flush(atShutdown: true)).WaitAsync(deadline.Token);
            if (!written) DropAtShutdown();
        }
        catch (Exception)
        {
            DropAtShutdown();
        }
    }

    /// <summary>
    /// One flush. Returns false when something could not be written, after putting it back in
    /// memory. With counting switched off it drops what is held and writes nothing.
    /// </summary>
    internal bool Flush(bool atShutdown = false)
    {
        if (!_options.CurrentValue.CountListens) return StopCounting();

        var drained = _counter.Drain();
        if (drained.Count == 0)
        {
            Interlocked.Exchange(ref _failing, 0);
            return true;
        }

        var written = 0;
        Exception? failure = null;
        foreach (var (key, count) in drained)
        {
            if (!_options.CurrentValue.CountListens) return StopCounting();

            try
            {
                _store.Add(key, count);
                written++;
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }
        }

        if (failure is null)
        {
            Interlocked.Exchange(ref _failing, 0);
            return true;
        }

        // Everything after the first failure was never tried, and the failed row may or may not
        // have been written, so only the rows that were written are left out.
        if (!_options.CurrentValue.CountListens) return StopCounting();

        _counter.MergeBack(drained.Skip(written).ToDictionary(p => p.Key, p => p.Value));

        // At shutdown the caller logs the one drop line, so the streak line stays out.
        if (Interlocked.Exchange(ref _failing, 1) == 0 && !atShutdown)
        {
            _logger.LogWarning(
                failure,
                "Read-aloud listen counts could not be written. They are kept in memory and the next flush will retry. "
                + "If the {Table} table is missing, the package migration has not run.",
                ListenRow.TableName);
        }

        return false;
    }

    /// <summary>Counting is off: drop what is held, end the failure streak, report nothing to retry.</summary>
    private bool StopCounting()
    {
        _counter.Clear();
        Interlocked.Exchange(ref _failing, 0);
        return true;
    }

    private void DropAtShutdown()
    {
        _counter.Clear();
        _logger.LogWarning("Read-aloud listen counts could not be written at shutdown and were dropped.");
    }
}
