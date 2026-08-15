using BaryoDev.Umbraco.ReadAloud.Caching;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
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
    private sealed class StubUmbracoBuilder(IServiceCollection services, IConfiguration config)
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

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.Combine(SiteRoot, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "TestSite";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = SiteRoot;
        public string EnvironmentName { get; set; } = "Development";
    }
}
