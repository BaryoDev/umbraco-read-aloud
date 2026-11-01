using BaryoDev.Umbraco.ReadAloud.Azure;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

internal static class AzureTestSupport
{
    /// <summary>Looks like nothing real, and is distinctive enough that finding it anywhere is a leak.</summary>
    public const string FakeKey = "test-key-do-not-leak-0123456789";

    public static string TempFolder() =>
        Path.Combine(Path.GetTempPath(), $"readaloud-keys-{Guid.NewGuid():N}");

    internal sealed class MemoryRecords : ICredentialRecords
    {
        public string? Value { get; set; }

        public string? Read() => Value;

        public void Write(string value) => Value = value;
    }

    internal sealed class FakeProbe(ProbeOutcome outcome) : IAzureProbe
    {
        public List<(string Region, string Key)> Calls { get; } = [];

        public Task<ProbeOutcome> RunAsync(string region, string key, CancellationToken ct)
        {
            Calls.Add((region, key));
            return Task.FromResult(outcome);
        }
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
        }
    }
}
