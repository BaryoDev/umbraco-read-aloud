using BaryoDev.Umbraco.ReadAloud.Azure;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class AzureSpeechOptionsTests
{
    // Looks like nothing real, and is distinctive enough that finding it in a message is a leak.
    private const string FakeKey = "test-key-do-not-leak-0123456789";

    private static readonly string Azure = AzureSpeechOptions.SectionName;

    [Fact]
    public void Azure_with_a_region_and_a_key_boots()
    {
        var provider = Compose(Settings("Azure", region: "southeastasia", key: FakeKey));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Surrounding_whitespace_on_the_region_and_key_is_ignored()
    {
        var provider = Compose(Settings("Azure", region: " southeastasia ", key: $" {FakeKey} "));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void A_missing_region_fails_the_boot_and_names_the_setting()
    {
        var provider = Compose(Settings("Azure", region: null, key: FakeKey));

        var error = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain($"{Azure}:Region", Case.Sensitive);
        error.Message.ShouldNotContain(FakeKey);
    }

    [Fact]
    public void A_missing_key_fails_the_boot_and_names_the_setting()
    {
        var provider = Compose(Settings("Azure", region: "southeastasia", key: null));

        var error = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain($"{Azure}:Key", Case.Sensitive);
        error.Message.ShouldNotContain($"{Azure}:Region");
    }

    [Fact]
    public void A_bad_region_is_refused_without_printing_the_key()
    {
        // The region ends up in a host name, so a value with a dot or a slash in it is refused.
        var provider = Compose(Settings("Azure", region: "evil.example/x", key: FakeKey));

        var error = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain($"{Azure}:Region", Case.Sensitive);
        error.Message.ShouldNotContain(FakeKey);
        error.Message.ShouldNotContain("evil.example");
    }

    [Fact]
    public void Azure_without_the_azure_package_fails_the_boot()
    {
        // Only the core composer runs, which is a site that set Provider and did not install the package.
        var provider = Compose(Settings("Azure", region: "southeastasia", key: FakeKey), installAzure: false);

        var error = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain("BaryoDev.Umbraco.ReadAloud.Azure", Case.Sensitive);
        error.Message.ShouldNotContain(FakeKey);
    }

    [Fact]
    public void An_unknown_provider_still_fails_with_the_azure_package_installed()
    {
        var provider = Compose(Settings("Polly", region: "southeastasia", key: FakeKey));

        var error = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain("Polly", Case.Sensitive);
        error.Message.ShouldContain("Edge", Case.Sensitive);
        error.Message.ShouldContain("Azure", Case.Sensitive);
    }

    [Fact]
    public void The_azure_provider_is_matched_without_case()
    {
        var provider = Compose(Settings("azure", region: "southeastasia", key: FakeKey));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        provider.GetRequiredService<IReadAloudEngine>().ShouldBeOfType<AzureSpeechEngine>();
    }

    [Fact]
    public void Installing_the_package_leaves_a_default_site_on_the_browser_voice()
    {
        // No Azure settings at all, and Provider left at its default. This is every existing site
        // that adds the package without opting in.
        var provider = Compose(new Dictionary<string, string?>());

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        provider.GetRequiredService<IReadAloudEngine>().ShouldBeOfType<BrowserVoiceEngine>();
    }

    [Fact]
    public void Provider_Azure_puts_the_azure_engine_behind_the_interface()
    {
        var provider = Compose(Settings("Azure", region: "southeastasia", key: FakeKey));

        provider.GetRequiredService<IReadAloudEngine>().ShouldBeOfType<AzureSpeechEngine>();
    }

    [Fact]
    public void A_missing_key_fails_at_startup_before_any_synthesis_and_names_the_setting()
    {
        // Options validation runs first, which is the designed path. The engine is never reached.
        var provider = Compose(Settings("Azure", region: "southeastasia", key: null));

        var error = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain($"{Azure}:Key", Case.Sensitive);
    }

    [Fact]
    public void A_missing_region_does_not_print_the_key_that_is_set()
    {
        var provider = Compose(Settings("Azure", region: null, key: "fake-key-value-123"));

        var error = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain($"{Azure}:Region", Case.Sensitive);
        error.Message.ShouldNotContain("fake-key-value-123");
    }

    private static Dictionary<string, string?> Settings(string provider, string? region, string? key)
    {
        var values = new Dictionary<string, string?>
        {
            [$"{ReadAloudOptions.SectionName}:Provider"] = provider,
        };

        if (region is not null) values[$"{Azure}:Region"] = region;
        if (key is not null) values[$"{Azure}:Key"] = key;

        return values;
    }

    /// <summary>Runs the real composers, core first as Umbraco orders them, and builds the container.</summary>
    private static ServiceProvider Compose(Dictionary<string, string?> values, bool installAzure = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWebHostEnvironment>(new ReadAloudComposerTests.StubEnvironment());

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var builder = new ReadAloudComposerTests.StubUmbracoBuilder(services, configuration);

        new ReadAloudComposer().Compose(builder);
        if (installAzure) new AzureSpeechComposer().Compose(builder);

        return services.BuildServiceProvider();
    }
}
