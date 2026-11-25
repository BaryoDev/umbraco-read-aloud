using BaryoDev.Umbraco.ReadAloud.Listens;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class ListenCounterTests
{
    private readonly ListenClock _clock = new();
    private readonly ListenOptions _options = new();
    private readonly WarningLog<ListenCounter> _log = new();

    private ListenCounter Counter() => new(_options, _clock, _log);

    [Fact]
    public void A_play_is_counted_under_the_node_the_voice_and_the_utc_day_from_the_injected_clock()
    {
        var counter = Counter();
        var node = Guid.NewGuid();

        counter.Add(node, "en-GB-SoniaNeural");

        counter.Snapshot().ShouldBe(new Dictionary<ListenKey, int>
        {
            [new ListenKey(node, "en-GB-SoniaNeural", ListenClock.Day)] = 1,
        });
    }

    [Fact]
    public void The_day_rolls_over_at_utc_midnight()
    {
        var counter = Counter();
        var node = Guid.NewGuid();
        _clock.Now = new DateTimeOffset(2026, 11, 16, 23, 59, 59, TimeSpan.Zero);

        counter.Add(node, "v");
        _clock.Advance(TimeSpan.FromSeconds(1));
        counter.Add(node, "v");

        var held = counter.Snapshot();
        held[new ListenKey(node, "v", 20261116)].ShouldBe(1);
        held[new ListenKey(node, "v", 20261117)].ShouldBe(1);
    }

    [Fact]
    public void Draining_takes_everything_and_later_adds_start_from_empty()
    {
        var counter = Counter();
        var node = Guid.NewGuid();
        counter.Add(node, "v");
        counter.Add(node, "v");

        var drained = counter.Drain();
        counter.Add(node, "v");

        drained.Values.Sum().ShouldBe(2);
        counter.Snapshot().Values.Sum().ShouldBe(1);
    }

    [Fact]
    public async Task Two_parallel_first_adds_for_one_key_end_at_2()
    {
        var counter = Counter();

        for (var round = 0; round < 300; round++)
        {
            var node = Guid.NewGuid();
            using var barrier = new Barrier(2);
            var adds = Enumerable.Range(0, 2)
                .Select(_ => Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    counter.Add(node, "v");
                }))
                .ToArray();
            await Task.WhenAll(adds);

            counter.Snapshot()[new ListenKey(node, "v", ListenClock.Day)].ShouldBe(2);
        }
    }

    [Fact]
    public void Turning_the_setting_off_counts_nothing_and_on_again_counts()
    {
        var counter = Counter();
        var node = Guid.NewGuid();

        _options.CurrentValue.CountListens = false;
        counter.Add(node, "v");
        counter.Snapshot().ShouldBeEmpty();

        _options.CurrentValue.CountListens = true;
        counter.Add(node, "v");
        counter.Snapshot().Values.Sum().ShouldBe(1);
    }

    [Fact]
    public void Past_the_cap_the_oldest_day_goes_first_with_one_log_line()
    {
        var counter = Counter();
        for (var i = 0; i < ListenCounter.MaxKeys / 2; i++) counter.Add(Guid.NewGuid(), "v");
        _clock.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < ListenCounter.MaxKeys / 2; i++) counter.Add(Guid.NewGuid(), "v");
        counter.Snapshot().Count.ShouldBe(ListenCounter.MaxKeys);
        _log.Warnings.ShouldBeEmpty();

        counter.Add(Guid.NewGuid(), "v");

        var held = counter.Snapshot();
        held.Count.ShouldBe(ListenCounter.MaxKeys / 2 + 1);
        held.Keys.ShouldAllBe(k => k.DayUtc == 20261117);
        _log.Warnings.Count.ShouldBe(1);
    }

    [Fact]
    public void A_key_already_held_is_counted_at_the_cap_without_dropping_anything()
    {
        var counter = Counter();
        var node = Guid.NewGuid();
        counter.Add(node, "v");
        for (var i = 1; i < ListenCounter.MaxKeys; i++) counter.Add(Guid.NewGuid(), "v");

        counter.Add(node, "v");

        counter.Snapshot().Count.ShouldBe(ListenCounter.MaxKeys);
        counter.Snapshot()[new ListenKey(node, "v", ListenClock.Day)].ShouldBe(2);
        _log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Merging_back_adds_on_top_of_what_arrived_since()
    {
        var counter = Counter();
        var node = Guid.NewGuid();
        counter.Add(node, "v");
        counter.Add(node, "v");
        var drained = counter.Drain();
        counter.Add(node, "v");

        counter.MergeBack(drained);

        counter.Snapshot()[new ListenKey(node, "v", ListenClock.Day)].ShouldBe(3);
    }

    [Fact]
    public void Merging_back_past_the_cap_drops_the_oldest_day_first()
    {
        var counter = Counter();
        for (var i = 0; i < 10; i++) counter.Add(Guid.NewGuid(), "v");
        var oldDay = counter.Drain();
        _clock.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < ListenCounter.MaxKeys; i++) counter.Add(Guid.NewGuid(), "v");

        counter.MergeBack(oldDay);

        counter.Snapshot().Count.ShouldBe(ListenCounter.MaxKeys);
        counter.Snapshot().Keys.ShouldAllBe(k => k.DayUtc == 20261117);
        _log.Warnings.Count.ShouldBe(1);
    }

    [Fact]
    public void Clearing_forgets_everything_held()
    {
        var counter = Counter();
        counter.Add(Guid.NewGuid(), "v");

        counter.Clear();

        counter.Snapshot().ShouldBeEmpty();
    }
}
