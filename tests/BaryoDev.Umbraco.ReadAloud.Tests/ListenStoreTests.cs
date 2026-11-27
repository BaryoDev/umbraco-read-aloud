using System.Data.Common;
using BaryoDev.Umbraco.ReadAloud.Listens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

[Collection(UmbracoCollection.Name)]
public class ListenStoreTests
{
    private readonly UmbracoSiteFixture _site;

    public ListenStoreTests(UmbracoSiteFixture site) => _site = site;

    private ListenStore Store() => new(_site.Services.GetRequiredService<IScopeProvider>());

    private int Listens(ListenKey key)
    {
        using var scope = _site.Services.GetRequiredService<IScopeProvider>().CreateScope();
        var rows = scope.Database.Fetch<int>(
            "SELECT Listens FROM baryoReadAloudListens WHERE NodeKey = @0 AND Voice = @1 AND DayUtc = @2",
            key.NodeKey, key.Voice, key.DayUtc);
        scope.Complete();
        return rows.Count == 0 ? 0 : rows[0];
    }

    private static ListenKey NewKey() => new(Guid.NewGuid(), "en-GB-SoniaNeural", ListenClock.Day);

    [Fact]
    public void Two_adds_for_one_key_end_as_one_row_holding_the_sum()
    {
        var store = Store();
        var key = NewKey();

        store.Add(key, 3);
        store.Add(key, 4);

        Listens(key).ShouldBe(7);
    }

    [Fact]
    public void Two_voices_on_one_day_and_one_voice_on_two_days_are_separate_rows()
    {
        var store = Store();
        var node = Guid.NewGuid();
        var sonia = new ListenKey(node, "en-GB-SoniaNeural", ListenClock.Day);
        var ryan = new ListenKey(node, "en-GB-RyanNeural", ListenClock.Day);
        var nextDay = new ListenKey(node, "en-GB-SoniaNeural", ListenClock.Day + 1);

        store.Add(sonia, 1);
        store.Add(ryan, 2);
        store.Add(nextDay, 4);
        store.Add(sonia, 8);

        Listens(sonia).ShouldBe(9);
        Listens(ryan).ShouldBe(2);
        Listens(nextDay).ShouldBe(4);
    }

    [Fact]
    public void A_non_ascii_voice_round_trips_unchanged()
    {
        var store = Store();
        var key = new ListenKey(Guid.NewGuid(), "zh-CN-\u6653\u6653Neural", ListenClock.Day);

        store.Add(key, 2);
        store.Add(key, 3);

        Listens(key).ShouldBe(5);
        using var scope = _site.Services.GetRequiredService<IScopeProvider>().CreateScope();
        scope.Database.Fetch<string>("SELECT Voice FROM baryoReadAloudListens WHERE NodeKey = @0", key.NodeKey)
            .ShouldBe([key.Voice]);
        scope.Complete();
    }

    [Fact]
    public void Another_server_inserting_first_is_met_with_an_update_not_a_failure()
    {
        var store = Store();
        var key = NewKey();
        store.BeforeInsert = k => new ListenStore(_site.Services.GetRequiredService<IScopeProvider>())
            .Add(k, 5);

        store.Add(key, 2);

        Listens(key).ShouldBe(7);
    }

    [Fact]
    public void An_insert_that_fails_and_leaves_no_row_to_update_throws()
    {
        var store = Store();
        var key = NewKey();
        store.BeforeInsert = _ => throw new TestDbException();

        Should.Throw<InvalidOperationException>(() => store.Add(key, 1));

        Listens(key).ShouldBe(0);
    }

    [Fact]
    public void The_table_has_four_columns_and_a_three_column_primary_key_that_refuses_a_duplicate()
    {
        using (var scope = _site.Services.GetRequiredService<IScopeProvider>().CreateScope())
        {
            var columns = scope.Database.Fetch<ColumnInfo>("SELECT name AS Name, pk AS Pk FROM pragma_table_info('baryoReadAloudListens')");
            scope.Complete();

            columns.Select(c => c.Name).OrderBy(n => n).ShouldBe(["DayUtc", "Listens", "NodeKey", "Voice"]);
            columns.Count(c => c.Pk > 0).ShouldBe(3);
            columns.Single(c => c.Name == "Listens").Pk.ShouldBe(0);
        }

        var key = NewKey();
        Store().Add(key, 1);
        using var dupe = _site.Services.GetRequiredService<IScopeProvider>().CreateScope();
        Should.Throw<DbException>(() => dupe.Database.Execute(
            "INSERT INTO baryoReadAloudListens (NodeKey, Voice, DayUtc, Listens) VALUES (@0, @1, @2, 1)",
            key.NodeKey, key.Voice, key.DayUtc));
    }

    [Fact]
    public void The_migration_guard_sees_the_real_table_and_a_missing_one()
    {
        using var scope = _site.Services.GetRequiredService<IScopeProvider>().CreateScope();

        CreateListensTable.IsMissing(scope.Database).ShouldBeFalse();
        CreateListensTable.IsMissing(scope.Database, "baryoReadAloudNoSuchTable").ShouldBeTrue();
        scope.Complete();
    }

    [Fact]
    public void The_migration_plan_is_named_for_the_package_and_ends_at_the_table_step()
    {
        var plan = new ListensMigrationPlan();

        plan.Name.ShouldBe("BaryoDev.Umbraco.ReadAloud");
        plan.FinalState.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void The_flush_service_is_registered_as_a_hosted_service_in_the_booted_site()
    {
        _site.Services.GetServices<IHostedService>().OfType<ListenFlushService>().Count().ShouldBe(1);
    }

    private sealed class ColumnInfo
    {
        public string Name { get; set; } = "";
        public int Pk { get; set; }
    }

    private sealed class TestDbException : DbException
    {
    }
}
