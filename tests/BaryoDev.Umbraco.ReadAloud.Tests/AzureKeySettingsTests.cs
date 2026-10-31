using System.Reflection;
using System.Text.Json;
using BaryoDev.Umbraco.ReadAloud.Azure;
using BaryoDev.Umbraco.ReadAloud.Controllers;
using BaryoDev.Umbraco.ReadAloud.Engine;
using BaryoDev.Umbraco.ReadAloud.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Umbraco.Cms.Web.Common.Authorization;
using static BaryoDev.Umbraco.ReadAloud.Tests.AzureTestSupport;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// The key saved from the backoffice: precedence, write-only, switching with no restart and a
/// store that cannot be read. No real key and no network: records live in memory and the
/// synthesis probe is a fake.
/// </summary>
public class AzureKeySettingsTests
{
    private static readonly string Azure = AzureSpeechOptions.SectionName;

    [Fact]
    public void Nothing_anywhere_means_the_browser_voice()
    {
        using var site = new Site();

        site.Engine.Provider.ShouldBe(ReadAloudOptions.BrowserProvider);
        site.Engine.SynthesizesOnServer.ShouldBeFalse();

        var status = site.Admin.GetStatus();
        status.KeySaved.ShouldBeFalse();
        status.Source.ShouldBe("none");
        status.Voice.ShouldBe("browser");
    }

    [Fact]
    public void A_saved_key_switches_to_azure_without_a_restart_and_remove_switches_back()
    {
        using var site = new Site();
        var engine = site.Engine;

        site.Admin.Save("southeastasia", FakeKey, "admin").Ok.ShouldBeTrue();

        engine.Provider.ShouldBe(ReadAloudOptions.AzureProvider);
        engine.SynthesizesOnServer.ShouldBeTrue();
        site.Admin.GetStatus().Source.ShouldBe("dashboard");

        site.Admin.Remove("admin").Ok.ShouldBeTrue();

        engine.Provider.ShouldBe(ReadAloudOptions.BrowserProvider);
        engine.SynthesizesOnServer.ShouldBeFalse();
        site.Admin.GetStatus().KeySaved.ShouldBeFalse();
    }

    [Fact]
    public void Config_wins_over_a_stored_key_and_locks_save_and_remove()
    {
        using var site = new Site(Settings("Azure", "westeurope", "config-key-0000"));
        site.Records.Value = null;

        var status = site.Admin.GetStatus();
        status.Source.ShouldBe("config");
        status.LockedByConfig.ShouldBeTrue();
        status.Region.ShouldBe("westeurope");

        site.Admin.Save("southeastasia", FakeKey, "admin").Reason.ShouldBe("locked");
        site.Admin.Remove("admin").Reason.ShouldBe("locked");
        site.Records.Value.ShouldBeNull();
        site.Engine.Provider.ShouldBe(ReadAloudOptions.AzureProvider);
    }

    [Fact]
    public void A_config_key_is_used_only_when_provider_is_azure()
    {
        using var site = new Site(new Dictionary<string, string?> { [$"{Azure}:Region"] = "westeurope", [$"{Azure}:Key"] = "config-key-0000" });

        var status = site.Admin.GetStatus();
        status.LockedByConfig.ShouldBeFalse();
        status.KeySaved.ShouldBeFalse();
        site.Engine.Provider.ShouldBe(ReadAloudOptions.BrowserProvider);
    }

    [Fact]
    public void Provider_azure_with_no_config_key_uses_the_stored_key()
    {
        using var site = new Site(new Dictionary<string, string?> { [$"{ReadAloudOptions.SectionName}:Provider"] = "Azure" });
        site.Admin.Save("southeastasia", FakeKey, "admin");

        site.Engine.Provider.ShouldBe(ReadAloudOptions.AzureProvider);
        site.Admin.GetStatus().Source.ShouldBe("dashboard");
    }

    [Fact]
    public void Edge_stays_edge_whatever_is_saved()
    {
        using var site = new Site(new Dictionary<string, string?> { [$"{ReadAloudOptions.SectionName}:Provider"] = "Edge" });
        site.Admin.Save("southeastasia", FakeKey, "admin");

        site.Engine.Provider.ShouldBe(ReadAloudOptions.EdgeProvider);
    }

    [Fact]
    public void No_response_model_carries_the_key_or_any_part_of_it()
    {
        using var site = new Site();

        var bodies = new List<string>
        {
            Json(site.Admin.GetStatus()),
            Json(site.Admin.TestAsync("southeastasia", FakeKey, default).GetAwaiter().GetResult()),
            Json(site.Admin.Save("southeastasia", FakeKey, "admin")),
            Json(site.Admin.GetStatus()),
            Json(site.Admin.Save("southeastasia", FakeKey + " bad", "admin")),
            Json(site.Admin.Save("bad.region/x", FakeKey, "admin")),
            Json(site.Admin.Remove("admin")),
        };

        foreach (var body in bodies)
        {
            body.ShouldNotContain(FakeKey);
            body.ShouldNotContain(FakeKey[..8]);
        }

        site.Logs.Lines.ShouldAllBe(line => !line.Contains(FakeKey));
    }

    [Fact]
    public void The_stored_record_does_not_hold_the_key_in_plain_text()
    {
        using var site = new Site();
        site.Admin.Save("southeastasia", FakeKey, "admin");

        site.Records.Value.ShouldNotBeNull();
        site.Records.Value.ShouldNotContain(FakeKey);
        site.Records.Value.ShouldContain("southeastasia");
    }

    [Theory]
    [InlineData("Rejected", "rejected")]
    [InlineData("Network", "network")]
    [InlineData("Quota", "quota")]
    [InlineData("Failed", "failed")]
    public async Task A_failed_test_gives_a_plain_reason_without_the_key(string outcome, string reason)
    {
        using var site = new Site(probe: Enum.Parse<ProbeOutcome>(outcome));

        var result = await site.Admin.TestAsync("southeastasia", FakeKey, default);

        result.Ok.ShouldBeFalse();
        result.Reason.ShouldBe(reason);
        result.Message.ShouldNotContain(FakeKey);
    }

    [Fact]
    public async Task Test_saves_nothing_and_uses_the_saved_key_when_the_field_is_empty()
    {
        using var site = new Site(probe: ProbeOutcome.Ok);

        (await site.Admin.TestAsync("southeastasia", FakeKey, default)).Ok.ShouldBeTrue();
        site.Records.Value.ShouldBeNull();
        site.Engine.Provider.ShouldBe(ReadAloudOptions.BrowserProvider);

        site.Admin.Save("southeastasia", FakeKey, "admin");
        await site.Admin.TestAsync("", "", default);

        site.Probe.Calls[^1].ShouldBe(("southeastasia", FakeKey));
    }

    [Fact]
    public async Task Test_with_no_key_anywhere_asks_for_one_and_calls_nothing()
    {
        using var site = new Site(probe: ProbeOutcome.Ok);

        var result = await site.Admin.TestAsync("southeastasia", "", default);

        result.Ok.ShouldBeFalse();
        result.Reason.ShouldBe("missing");
        site.Probe.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Saving_with_a_blank_key_keeps_the_saved_key_and_changes_the_region()
    {
        using var site = new Site();
        site.Admin.Save("southeastasia", FakeKey, "admin");

        site.Admin.Save("westeurope", "", "admin").Ok.ShouldBeTrue();

        var credential = site.Resolver.Current;
        credential!.Region.ShouldBe("westeurope");
        credential.Key.ShouldBe(FakeKey);
    }

    [Fact]
    public void Saving_with_no_key_and_nothing_saved_is_refused()
    {
        using var site = new Site();

        site.Admin.Save("southeastasia", "", "admin").Reason.ShouldBe("missing");
    }

    [Fact]
    public void A_value_that_cannot_be_decrypted_means_the_browser_voice_and_a_dashboard_notice()
    {
        var records = new MemoryRecords();
        using var first = new Site(records: records);
        first.Admin.Save("southeastasia", FakeKey, "admin");

        // Same stored record, a different key ring: what a redeploy that lost the folder looks like.
        using var second = new Site(records: records);

        Should.NotThrow(() => second.Engine.Provider).ShouldBe(ReadAloudOptions.BrowserProvider);
        second.Engine.SynthesizesOnServer.ShouldBeFalse();

        var status = second.Admin.GetStatus();
        status.Unreadable.ShouldBeTrue();
        status.KeySaved.ShouldBeFalse();
        Json(status).ShouldNotContain(FakeKey);

        second.Logs.Lines.ShouldContain(line => line.StartsWith("Warning"));
        second.Logs.Lines.ShouldAllBe(line => !line.Contains(FakeKey));

        // Entering it again repairs it.
        second.Admin.Save("southeastasia", FakeKey, "admin").Ok.ShouldBeTrue();
        second.Engine.Provider.ShouldBe(ReadAloudOptions.AzureProvider);
    }

    [Fact]
    public void A_record_that_is_not_json_does_not_throw()
    {
        using var site = new Site();
        site.Records.Value = "{{ not json";

        Should.NotThrow(() => site.Engine.Provider);
        Should.NotThrow(() => site.Admin.GetStatus());
    }

    [Fact]
    public void An_empty_config_boots_with_the_azure_package_and_no_stored_key()
    {
        using var site = new Site();

        Should.NotThrow(() => site.Services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Provider_azure_with_no_config_key_boots_when_a_key_is_stored()
    {
        using var site = new Site(new Dictionary<string, string?> { [$"{ReadAloudOptions.SectionName}:Provider"] = "Azure" });
        site.Admin.Save("southeastasia", FakeKey, "admin");

        Should.NotThrow(() => site.Services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Provider_azure_with_nothing_set_anywhere_still_fails_the_boot()
    {
        using var site = new Site(new Dictionary<string, string?> { [$"{ReadAloudOptions.SectionName}:Provider"] = "Azure" });

        var error = Should.Throw<OptionsValidationException>(
            () => site.Services.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain($"{Azure}:Key", Case.Sensitive);
    }

    [Fact]
    public void A_bad_config_region_still_fails_the_boot_when_a_key_is_stored()
    {
        var values = Settings("Azure", "evil.example/x", null);
        using var site = new Site(values);
        site.Admin.Save("southeastasia", FakeKey, "admin");

        var error = Should.Throw<OptionsValidationException>(
            () => site.Services.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain($"{Azure}:Region", Case.Sensitive);
    }

    [Fact]
    public void The_cache_key_follows_the_provider_and_never_the_key()
    {
        using var site = new Site();
        var request = new SynthesisRequest { Text = "Hello.", Voice = "en-PH-RosaNeural" };

        site.Admin.Save("southeastasia", FakeKey, "admin");
        var first = request.CacheKey(site.Engine.Provider!);
        site.Admin.Save("southeastasia", FakeKey + "-new", "admin");
        var second = request.CacheKey(site.Engine.Provider!);

        second.ShouldBe(first);
        first.ShouldNotContain(FakeKey);
    }

    [Fact]
    public void Every_settings_action_needs_settings_section_access()
    {
        var type = typeof(ReadAloudSettingsController);

        type.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ShouldContain(a => a.Policy == AuthorizationPolicies.SectionAccessSettings);
        type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).ShouldBeEmpty();

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            method.GetCustomAttributes<AllowAnonymousAttribute>().ShouldBeEmpty(method.Name);
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static Dictionary<string, string?> Settings(string provider, string? region, string? key)
    {
        var values = new Dictionary<string, string?> { [$"{ReadAloudOptions.SectionName}:Provider"] = provider };
        if (region is not null) values[$"{Azure}:Region"] = region;
        if (key is not null) values[$"{Azure}:Key"] = key;
        return values;
    }

    /// <summary>The real composers over in-memory records, a private key ring in a temp folder and a fake probe.</summary>
    private sealed class Site : IDisposable
    {
        private readonly string _keys = TempFolder();
        private readonly ServiceProvider _provider;

        public Site(
            Dictionary<string, string?>? values = null,
            ProbeOutcome probe = ProbeOutcome.Ok,
            MemoryRecords? records = null)
        {
            Records = records ?? new MemoryRecords();
            Probe = new FakeProbe(probe);
            Logs = new CapturingLogger<AzureCredentialStore>();

            values ??= new Dictionary<string, string?>();
            values[$"{ReadAloudOptions.SectionName}:KeysPath"] = _keys;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new ReadAloudComposerTests.StubEnvironment());
            services.AddSingleton<ICredentialRecords>(Records);

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var builder = new ReadAloudComposerTests.StubUmbracoBuilder(services, configuration);

            new ReadAloudComposer().Compose(builder);
            new AzureSpeechComposer().Compose(builder);

            services.AddSingleton<IAzureProbe>(Probe);
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<AzureCredentialStore>>(Logs);

            _provider = services.BuildServiceProvider();
        }

        public MemoryRecords Records { get; }

        public FakeProbe Probe { get; }

        public CapturingLogger<AzureCredentialStore> Logs { get; }

        public IServiceProvider Services => _provider;

        public IReadAloudEngine Engine => _provider.GetRequiredService<IReadAloudEngine>();

        public IReadAloudAzureAdmin Admin => _provider.GetRequiredService<IReadAloudAzureAdmin>();

        public AzureCredentialResolver Resolver => _provider.GetRequiredService<AzureCredentialResolver>();

        public void Dispose()
        {
            _provider.Dispose();
            try { Directory.Delete(_keys, recursive: true); } catch (IOException) { }
        }
    }
}
