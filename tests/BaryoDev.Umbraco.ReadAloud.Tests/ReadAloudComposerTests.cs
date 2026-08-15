using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class ReadAloudComposerTests
{
    [Fact]
    public void A_relative_cache_path_is_resolved_against_the_site_root()
    {
        // The default CachePath is relative, so this is the branch every production site takes and
        // the one no test covered: the fixture configures an absolute path, so the composer's
        // Path.IsPathRooted check was always true.
        var resolved = ReadAloudComposer.ResolveCachePath(
            "App_Data/BaryoDev/ReadAloud", Path.Combine("srv", "site"));

        resolved.ShouldBe(Path.Combine("srv", "site", "App_Data/BaryoDev/ReadAloud"));
    }

    [Fact]
    public void A_relative_cache_path_does_not_follow_the_working_directory()
    {
        // Resolving relative to the process working directory is the failure this exists to
        // prevent. It differs between dotnet run, IIS and a test host, so the cache would land
        // somewhere different in production than anywhere it was tested, and a site would quietly
        // re-synthesize everything after a deployment.
        var resolved = ReadAloudComposer.ResolveCachePath("cache", Path.Combine("srv", "site"));

        resolved.ShouldNotStartWith(Directory.GetCurrentDirectory());
        resolved.ShouldBe(Path.Combine("srv", "site", "cache"));
    }

    [Fact]
    public void An_absolute_cache_path_is_left_alone()
    {
        // A site putting the cache on another volume must not have the site root prepended to it.
        var absolute = Path.Combine(Path.GetTempPath(), "read-aloud-cache");

        ReadAloudComposer.ResolveCachePath(absolute, Path.Combine("srv", "site")).ShouldBe(absolute);
    }
}
