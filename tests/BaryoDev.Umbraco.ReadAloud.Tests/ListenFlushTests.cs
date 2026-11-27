using BaryoDev.Umbraco.ReadAloud.Listens;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class ListenFlushTests
{
    private readonly ListenClock _clock = new();
    private readonly ListenOptions _options = new();
    private readonly WarningLog<ListenCounter> _counterLog = new();
    private readonly WarningLog<ListenFlushService> _log = new();
    private readonly FakeListenStore _store = new();
    private readonly ListenCounter _counter;
    private bool _ready = true;

    public ListenFlushTests() => _counter = new ListenCounter(_options, _clock, _counterLog);

    private ListenFlushService Service(TimeSpan? shutdown = null) =>
        new(_counter, _store, () => _ready, _options, _log,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), shutdown ?? TimeSpan.FromSeconds(5));

    private static ListenKey Key(Guid node, string voice = "v") => new(node, voice, ListenClock.Day);

    [Fact]
    public void Two_flushes_add_up_in_the_store()
    {
        var service = Service();
        var node = Guid.NewGuid();

        _counter.Add(node, "v");
        _counter.Add(node, "v");
        service.Flush().ShouldBeTrue();
        _counter.Add(node, "v");
        service.Flush().ShouldBeTrue();

        _store.Rows[Key(node)].ShouldBe(3);
        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void A_count_added_while_a_flush_is_writing_is_written_by_the_next_flush_once()
    {
        var service = Service();
        var node = Guid.NewGuid();
        _counter.Add(node, "v");
        var added = false;
        _store.OnAdd = () =>
        {
            if (added) return;
            added = true;
            _counter.Add(node, "v");
        };

        service.Flush().ShouldBeTrue();
        _store.Rows[Key(node)].ShouldBe(1);
        _counter.Snapshot()[Key(node)].ShouldBe(1);

        service.Flush().ShouldBeTrue();
        _store.Rows[Key(node)].ShouldBe(2);
        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void A_failed_flush_keeps_its_counts_and_the_retry_does_not_write_a_row_twice()
    {
        var service = Service();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        _counter.Add(first, "v");
        _counter.Add(second, "v");
        _counter.Add(third, "v");
        var failing = true;
        _store.FailWhen = k => failing && k.NodeKey == second;

        service.Flush().ShouldBeFalse();
        failing = false;
        service.Flush().ShouldBeTrue();

        foreach (var node in new[] { first, second, third }) _store.Rows[Key(node)].ShouldBe(1);
        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void A_failure_streak_logs_one_warning_and_a_new_streak_logs_another()
    {
        var service = Service();
        var node = Guid.NewGuid();
        _counter.Add(node, "v");
        var failing = true;
        _store.FailWhen = _ => failing;

        service.Flush().ShouldBeFalse();
        service.Flush().ShouldBeFalse();
        service.Flush().ShouldBeFalse();
        _log.Warnings.Count.ShouldBe(1);

        failing = false;
        service.Flush().ShouldBeTrue();
        failing = true;
        _counter.Add(node, "v");
        service.Flush().ShouldBeFalse();

        _log.Warnings.Count.ShouldBe(2);
    }

    [Fact]
    public void With_the_setting_off_held_counts_are_dropped_and_nothing_is_written()
    {
        var service = Service();
        _counter.Add(Guid.NewGuid(), "v");
        _options.CurrentValue.CountListens = false;

        service.Flush().ShouldBeTrue();

        _store.Rows.ShouldBeEmpty();
        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task Stopping_with_the_setting_off_writes_nothing()
    {
        var service = Service();
        _counter.Add(Guid.NewGuid(), "v");
        _options.CurrentValue.CountListens = false;

        await service.StopAsync(CancellationToken.None);

        _store.Rows.ShouldBeEmpty();
        _store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Stopping_flushes_what_is_held_once()
    {
        var service = Service();
        var node = Guid.NewGuid();
        _counter.Add(node, "v");
        _counter.Add(node, "v");

        await service.StopAsync(CancellationToken.None);

        _store.Rows[Key(node)].ShouldBe(2);
        _store.Calls.ShouldBe(1);
        _log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stopping_with_a_failing_store_does_not_throw_and_drops_the_counts_with_one_warning()
    {
        var service = Service();
        _counter.Add(Guid.NewGuid(), "v");
        _store.FailWhen = _ => true;

        await service.StopAsync(CancellationToken.None);

        _counter.Snapshot().ShouldBeEmpty();
        _log.Warnings.Count.ShouldBe(1);
        _log.Warnings[0].ShouldContain("shutdown");
    }

    [Fact]
    public void A_new_streak_after_the_setting_was_switched_off_logs_again()
    {
        var service = Service();
        _counter.Add(Guid.NewGuid(), "v");
        _store.FailWhen = _ => true;
        service.Flush().ShouldBeFalse();

        _options.CurrentValue.CountListens = false;
        service.Flush().ShouldBeTrue();
        _options.CurrentValue.CountListens = true;
        _counter.Add(Guid.NewGuid(), "v");
        service.Flush().ShouldBeFalse();

        _log.Warnings.Count.ShouldBe(2);
    }

    [Fact]
    public void A_new_streak_after_an_empty_flush_logs_again()
    {
        var service = Service();
        _counter.Add(Guid.NewGuid(), "v");
        _store.FailWhen = _ => true;
        service.Flush().ShouldBeFalse();

        _counter.Clear();
        service.Flush().ShouldBeTrue();
        _counter.Add(Guid.NewGuid(), "v");
        service.Flush().ShouldBeFalse();

        _log.Warnings.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_flush_that_cannot_read_the_options_logs_a_warning_keeps_running_and_writes_later()
    {
        var options = new ThrowingOptions { Throwing = true };
        var service = new ListenFlushService(
            _counter, _store, () => _ready, options, _log,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
        var node = Guid.NewGuid();
        _counter.Add(node, "v");

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_log.Warnings.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);

        _log.Warnings.Count.ShouldBeGreaterThanOrEqualTo(2);
        service.ExecuteTask!.IsFaulted.ShouldBeFalse();
        service.ExecuteTask.IsCompleted.ShouldBeFalse();
        _store.Calls.ShouldBe(0);

        options.Throwing = false;
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (_store.Calls == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await service.StopAsync(CancellationToken.None);

        _store.Rows[Key(node)].ShouldBe(1);
    }

    private sealed class ThrowingOptions : IOptionsMonitor<ReadAloudOptions>
    {
        private readonly ReadAloudOptions _value = new() { PropertyAlias = UmbracoSiteFixture.PropertyAlias };
        public volatile bool Throwing;

        public ReadAloudOptions CurrentValue =>
            Throwing ? throw new OptionsValidationException("", typeof(ReadAloudOptions), ["bad reload"]) : _value;

        public ReadAloudOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ReadAloudOptions, string?> listener) => null;
    }

    [Fact]
    public async Task Stopping_with_a_blocked_store_returns_within_the_shutdown_timeout()
    {
        using var block = new ManualResetEventSlim(false);
        var service = Service(TimeSpan.FromMilliseconds(200));
        _counter.Add(Guid.NewGuid(), "v");
        _store.Block = block;

        var started = DateTime.UtcNow;
        await service.StopAsync(CancellationToken.None);
        var took = DateTime.UtcNow - started;
        block.Set();

        took.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        _log.Warnings.ShouldContain(w => w.Contains("shutdown"));
    }

    [Fact]
    public async Task Stopping_gives_the_final_flush_only_the_time_left_of_one_deadline()
    {
        using var gate = new ManualResetEventSlim(false);
        try
        {
            // Two separate waits of this length would take over 2 seconds, one shared deadline about 1.2.
            var service = Service(TimeSpan.FromMilliseconds(1200));
            _counter.Add(Guid.NewGuid(), "v");
            _store.Block = gate;

            await service.StartAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_counter.Snapshot().Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
            _counter.Add(Guid.NewGuid(), "v");

            var stop = service.StopAsync(CancellationToken.None);
            var first = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(2)));

            first.ShouldBeSameAs(stop);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public void Switching_the_setting_off_during_a_flush_stops_the_writing_and_drops_the_rest()
    {
        var service = Service();
        _counter.Add(Guid.NewGuid(), "a");
        _counter.Add(Guid.NewGuid(), "b");
        _store.OnAdd = () => _options.CurrentValue.CountListens = false;

        service.Flush().ShouldBeTrue();

        _store.Rows.Count.ShouldBe(1);
        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void A_failed_write_after_the_setting_went_off_is_not_put_back()
    {
        var service = Service();
        _counter.Add(Guid.NewGuid(), "a");
        _counter.Add(Guid.NewGuid(), "b");
        _store.OnAdd = () => _options.CurrentValue.CountListens = false;
        _store.FailWhen = _ => true;

        service.Flush().ShouldBeTrue();

        _store.Rows.ShouldBeEmpty();
        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_is_written_until_the_runtime_is_ready()
    {
        var service = Service();
        var node = Guid.NewGuid();
        _counter.Add(node, "v");
        _ready = false;

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        _store.Calls.ShouldBe(0);

        _ready = true;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_store.Calls == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await service.StopAsync(CancellationToken.None);

        _store.Rows[Key(node)].ShouldBe(1);
    }

    [Fact]
    public async Task Stopping_before_the_runtime_is_ready_writes_nothing_and_does_not_throw()
    {
        var service = Service();
        _counter.Add(Guid.NewGuid(), "v");
        _ready = false;

        await service.StopAsync(CancellationToken.None);

        _store.Calls.ShouldBe(0);
    }

    [Fact]
    public void A_play_either_side_of_utc_midnight_is_two_rows()
    {
        var service = Service();
        var node = Guid.NewGuid();
        _clock.Now = new DateTimeOffset(2026, 11, 16, 23, 59, 59, TimeSpan.Zero);
        _counter.Add(node, "v");
        _clock.Advance(TimeSpan.FromSeconds(1));
        _counter.Add(node, "v");

        service.Flush();

        _store.Rows[new ListenKey(node, "v", 20261116)].ShouldBe(1);
        _store.Rows[new ListenKey(node, "v", 20261117)].ShouldBe(1);
    }
}
