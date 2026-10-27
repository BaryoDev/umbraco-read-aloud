using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Options;
using Shouldly;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Logging;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class ReadAloudComposerTests
{
    private const string SiteRoot = "/srv/site";

    [Fact]
    public void A_relative_cache_path_is_resolved_against_the_site_root()
    {
        // The default CachePath is relative, so this is the branch every production site takes and
        // the one the booted fixture cannot cover: it configures an absolute path, so the
        // composer's Path.IsPathRooted check is always true there.
        var cache = ComposedCache("App_Data/BaryoDev/ReadAloud");

        cache.Root.ShouldBe(Path.Combine(SiteRoot, "App_Data/BaryoDev/ReadAloud"));
    }

    [Fact]
    public void A_relative_cache_path_does_not_follow_the_working_directory()
    {
        // Resolving relative to the process working directory is the failure this prevents. It
        // differs between dotnet run, IIS and a test host, so the cache would land somewhere
        // different in production than anywhere it was tested and a site would silently
        // re-synthesize everything after a deployment.
        var cache = ComposedCache("cache");

        cache.Root.ShouldBe(Path.Combine(SiteRoot, "cache"));
        cache.Root.ShouldNotStartWith(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void An_absolute_cache_path_is_left_alone()
    {
        // A site putting the cache on another volume must not have the site root prepended.
        var absolute = Path.Combine(Path.GetTempPath(), "read-aloud-cache");

        ComposedCache(absolute).Root.ShouldBe(absolute);
    }

    [Fact]
    public void A_provider_this_version_does_not_implement_fails_the_boot()
    {
        // Provider was read by nothing, so a site that configured "AzureSpeech" after reading the
        // documentation booted cleanly and kept talking to the unofficial Edge endpoint, believing
        // it had a contracted one. A startup failure is the only honest answer.
        var validator = ComposedProvider("AzureSpeech").GetRequiredService<IStartupValidator>();

        var error = Should.Throw<OptionsValidationException>(() => validator.Validate());

        error.Message.ShouldContain("AzureSpeech", Case.Sensitive,
            "the message has to name the value that was configured");
        error.Message.ShouldContain("Browser", Case.Sensitive,
            "the message has to name the provider that is supported");
    }

    [Fact]
    public void The_supported_provider_boots()
    {
        // The other half. Refusing every value would satisfy the test above on its own, and would
        // stop every site that configures nothing, since Browser is the default.
        var validator = ComposedProvider("Edge").GetRequiredService<IStartupValidator>();

        Should.NotThrow(() => validator.Validate());
    }

    [Fact]
    public void The_supported_provider_is_matched_without_case()
    {
        // Hand-typed into a configuration file, exactly like the document type aliases the
        // controller already compares without case.
        var validator = ComposedProvider("edge").GetRequiredService<IStartupValidator>();

        Should.NotThrow(() => validator.Validate());
    }

    [Fact]
    public void The_default_provider_is_the_browser_voice()
    {
        new ReadAloudOptions().Provider.ShouldBe(ReadAloudOptions.BrowserProvider);
        ReadAloudOptions.BrowserProvider.ShouldBe("Browser");
    }

    [Fact]
    public void Browser_registers_no_remote_engine_and_boots()
    {
        var provider = ComposedProvider(null);

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        provider.GetRequiredService<IReadAloudEngine>().ShouldBeOfType<BrowserVoiceEngine>();
        provider.GetRequiredService<IReadAloudEngine>().ShouldNotBeOfType<EdgeTtsEngine>();

        var explicitBrowser = ComposedProvider("browser");
        Should.NotThrow(() => explicitBrowser.GetRequiredService<IStartupValidator>().Validate());
        explicitBrowser.GetRequiredService<IReadAloudEngine>().ShouldBeOfType<BrowserVoiceEngine>();
    }

    [Fact]
    public async Task The_browser_engine_refuses_to_synthesize()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new BrowserVoiceEngine().SynthesizeAsync(new SynthesisRequest { Text = "Hello." }));
    }

    [Fact]
    public void Edge_still_composes_and_logs_a_deprecation_warning()
    {
        var logs = new CapturingLoggerProvider();
        var provider = ComposedProvider("Edge", logs);

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        provider.GetRequiredService<IReadAloudEngine>().ShouldBeOfType<EdgeTtsEngine>();

        var warning = logs.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("deprecated");
        warning.ShouldContain("issue 18");
        warning.ShouldContain("Azure");
    }

    [Fact]
    public void Browser_logs_no_deprecation_warning()
    {
        var logs = new CapturingLoggerProvider();
        var provider = ComposedProvider("Browser", logs);

        provider.GetRequiredService<IStartupValidator>().Validate();

        logs.Warnings.ShouldBeEmpty();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(Warnings);

        public void Dispose()
        {
        }

        private sealed class Capture(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) warnings.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>Runs the composer for real over a configured provider and builds the container.</summary>
    private static ServiceProvider ComposedProvider(string? provider, ILoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (logs is not null) logging.AddProvider(logs);
        });
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(provider is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [$"{ReadAloudOptions.SectionName}:Provider"] = provider })
            .Build();

        new ReadAloudComposer().Compose(new StubUmbracoBuilder(services, configuration));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Runs the composer for real and resolves the cache it registered.
    /// </summary>
    /// <remarks>
    /// Through Compose rather than by calling ResolveCachePath directly, because the helper being
    /// correct proves nothing about the registration using it. Reverting the factory to
    /// options.CachePath passed a test that only exercised the helper.
    /// </remarks>
    private static DiskAudioCache ComposedCache(string configuredPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ReadAloudOptions.SectionName}:CachePath"] = configuredPath,
            })
            .Build();

        new ReadAloudComposer().Compose(new StubUmbracoBuilder(services, configuration));

        return services.BuildServiceProvider()
            .GetRequiredService<IAudioCache>()
            .ShouldBeOfType<DiskAudioCache>();
    }

    /// <summary>
    /// Carries the two things the composer touches, and refuses everything else.
    /// </summary>
    /// <remarks>
    /// A real IUmbracoBuilder needs a TypeLoader and a built service collection, which is a whole
    /// Umbraco boot. The composer reads Services and Config and nothing else, so anything else
    /// throwing is a truthful way to find out if that ever stops being so.
    /// </remarks>
    internal sealed class StubUmbracoBuilder(IServiceCollection services, IConfiguration config)
        : IUmbracoBuilder
    {
        public IServiceCollection Services { get; } = services;

        public IConfiguration Config { get; } = config;

        public TypeLoader TypeLoader => throw new NotSupportedException();

        public ILoggerFactory BuilderLoggerFactory => throw new NotSupportedException();

        public global::Umbraco.Cms.Core.Hosting.IHostingEnvironment? BuilderHostingEnvironment =>
            throw new NotSupportedException();

        public IProfiler Profiler => throw new NotSupportedException();

        public AppCaches AppCaches => throw new NotSupportedException();

        // Explicit, so the constraints come from the interface rather than being restated here
        // and having to match it exactly.
        TBuilder IUmbracoBuilder.WithCollectionBuilder<TBuilder>() => throw new NotSupportedException();

        public void Build() => throw new NotSupportedException();
    }

    internal sealed class StubEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.Combine(SiteRoot, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "TestSite";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = SiteRoot;
        public string EnvironmentName { get; set; } = "Development";
    }
}
