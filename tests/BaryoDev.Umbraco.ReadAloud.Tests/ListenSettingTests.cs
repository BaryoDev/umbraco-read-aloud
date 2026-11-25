using BaryoDev.Umbraco.ReadAloud.Listens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class ListenSettingTests
{
    private static ReadAloudOptionsValidation Validation() =>
        new([], NullLogger<ReadAloudOptionsValidation>.Instance);

    [Fact]
    public void Counting_is_on_by_default()
    {
        new ReadAloudOptions().CountListens.ShouldBeTrue();
    }

    [Fact]
    public void A_default_voice_of_100_characters_passes_and_101_fails()
    {
        Validation().Validate(null, new ReadAloudOptions { DefaultVoice = new string('a', 100) }).Succeeded.ShouldBeTrue();
        Validation().Validate(null, new ReadAloudOptions { DefaultVoice = new string('a', 101) }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void An_allowed_voice_of_100_characters_passes_and_101_fails()
    {
        Validation().Validate(null, new ReadAloudOptions { AllowedVoices = [new string('a', 100)] }).Succeeded.ShouldBeTrue();
        Validation().Validate(null, new ReadAloudOptions { AllowedVoices = [new string('a', 101)] }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void The_voice_length_limit_applies_in_browser_mode_too()
    {
        var options = new ReadAloudOptions
        {
            Provider = ReadAloudOptions.BrowserProvider,
            AllowedVoices = [new string('a', 101)],
        };

        Validation().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void The_composer_registers_the_counter_and_the_flush_service()
    {
        var provider = ReadAloudComposerTests_Provider();

        provider.GetRequiredService<ListenCounter>().ShouldNotBeNull();
        provider.GetServices<IHostedService>().OfType<ListenFlushService>().Count().ShouldBe(1);
    }

    [Fact]
    public void The_composer_keeps_a_time_provider_the_site_registered_first()
    {
        var mine = new ListenClock();
        var provider = ReadAloudComposerTests_Provider(mine);

        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(mine);
    }

    [Fact]
    public void The_composer_registers_the_system_time_provider_when_the_site_has_none()
    {
        ReadAloudComposerTests_Provider().GetRequiredService<TimeProvider>().ShouldBeSameAs(TimeProvider.System);
    }

    private static ServiceProvider ReadAloudComposerTests_Provider(TimeProvider? first = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new ReadAloudComposerTests.StubEnvironment());
        if (first is not null) services.AddSingleton(first);

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        new ReadAloudComposer().Compose(new ReadAloudComposerTests.StubUmbracoBuilder(services, configuration));
        return services.BuildServiceProvider();
    }
}
