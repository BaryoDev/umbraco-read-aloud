using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Listens;

/// <summary>
/// Counts plays in memory per article, voice and UTC day until the flush service writes them.
/// </summary>
/// <remarks>
/// Holds a count and a key and nothing else, so there is nothing here that could say who listened.
/// The day is part of the key, so a flush that straddles UTC midnight splits correctly.
/// </remarks>
public sealed class ListenCounter
{
    /// <summary>The most keys held in memory. Past it the oldest day is dropped first.</summary>
    internal const int MaxKeys = 10_000;

    private readonly IOptionsMonitor<ReadAloudOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ListenCounter>? _logger;
    private readonly object _gate = new();
    private Dictionary<ListenKey, int> _counts = [];

    /// <summary>Creates the counter.</summary>
    public ListenCounter(
        IOptionsMonitor<ReadAloudOptions> options,
        TimeProvider time,
        ILogger<ListenCounter>? logger = null)
    {
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Counts one play of a node in a voice, today in UTC. Does nothing when counting is off.</summary>
    public void Add(Guid nodeKey, string voice)
    {
        if (!_options.CurrentValue.CountListens) return;

        var key = new ListenKey(nodeKey, voice, ListenKey.DayOf(_time.GetUtcNow()));

        lock (_gate)
        {
            if (!_counts.ContainsKey(key) && _counts.Count >= MaxKeys) DropOldestDay();

            _counts[key] = _counts.GetValueOrDefault(key) + 1;
        }
    }

    /// <summary>Takes everything counted so far and starts again from empty.</summary>
    internal Dictionary<ListenKey, int> Drain()
    {
        lock (_gate)
        {
            var taken = _counts;
            _counts = [];
            return taken;
        }
    }

    /// <summary>Puts drained counts back after a failed write, on top of whatever arrived since.</summary>
    internal void MergeBack(IReadOnlyDictionary<ListenKey, int> counts)
    {
        lock (_gate)
        {
            foreach (var (key, n) in counts) _counts[key] = _counts.GetValueOrDefault(key) + n;

            while (_counts.Count > MaxKeys)
            {
                if (!DropOldestDay()) break;
            }
        }
    }

    /// <summary>Forgets everything held. Used when counting is switched off.</summary>
    internal void Clear()
    {
        lock (_gate) _counts = [];
    }

    /// <summary>What is held now, for tests.</summary>
    internal IReadOnlyDictionary<ListenKey, int> Snapshot()
    {
        lock (_gate) return new Dictionary<ListenKey, int>(_counts);
    }

    /// <summary>Removes every key of the oldest day held. Call with the lock taken.</summary>
    private bool DropOldestDay()
    {
        if (_counts.Count == 0) return false;

        var oldest = _counts.Keys.Min(k => k.DayUtc);
        var dropped = 0;
        foreach (var key in _counts.Keys.Where(k => k.DayUtc == oldest).ToList())
        {
            _counts.Remove(key);
            dropped++;
        }

        _logger?.LogWarning(
            "Read-aloud listen counts reached {Cap} keys in memory. Dropped {Dropped} keys for day {Day}.",
            MaxKeys,
            dropped,
            oldest);

        return true;
    }
}
