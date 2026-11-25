using BaryoDev.Umbraco.ReadAloud.Listens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>A clock a test can move. Starts at noon UTC on 16 November 2026.</summary>
internal sealed class ListenClock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public const int Day = 20261116;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Options a test can change after the monitor is handed out.</summary>
internal sealed class ListenOptions : IOptionsMonitor<ReadAloudOptions>
{
    public ReadAloudOptions CurrentValue { get; } = new() { PropertyAlias = UmbracoSiteFixture.PropertyAlias };

    public ReadAloudOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<ReadAloudOptions, string?> listener) => null;
}

/// <summary>Keeps the warnings a component logs.</summary>
internal sealed class WarningLog<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception));
    }
}

/// <summary>A store that adds into a dictionary, and fails or blocks when a test says so.</summary>
internal sealed class FakeListenStore : IListenStore
{
    public Dictionary<ListenKey, int> Rows { get; } = [];
    public int Calls;
    public Func<ListenKey, bool>? FailWhen;
    public Action? OnAdd;
    public ManualResetEventSlim? Block;

    public void Add(ListenKey key, int count)
    {
        Block?.Wait();
        OnAdd?.Invoke();
        if (FailWhen?.Invoke(key) == true) throw new InvalidOperationException("no such table: baryoReadAloudListens");

        lock (Rows) Rows[key] = Rows.GetValueOrDefault(key) + count;
        Interlocked.Increment(ref Calls);
    }
}
