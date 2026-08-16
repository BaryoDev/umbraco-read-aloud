using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace BaryoDev.Umbraco.ReadAloud;

/// <summary>
/// Registers everything the endpoint needs, so installing the package is the whole install.
/// </summary>
public class ReadAloudComposer : IComposer
{
    /// <inheritdoc />
    public void Compose(IUmbracoBuilder builder)
    {
        // ValidateOnStart rather than validating lazily on first read. Lazily, the first reader is
        // a visitor's request, so a misconfigured site looks healthy until someone presses Listen
        // and then answers 503 for a reason nothing on the page explains.
        builder.Services.AddOptions<ReadAloudOptions>()
            .Bind(builder.Config.GetSection(ReadAloudOptions.SectionName))
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<ReadAloudOptions>, ReadAloudProviderValidation>();

        builder.Services.AddSingleton<IReadAloudEngine, EdgeTtsEngine>();

        builder.Services.AddSingleton<IAudioCache>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<ReadAloudOptions>>().CurrentValue;
            var environment = sp.GetRequiredService<IWebHostEnvironment>();

            var root = ResolveCachePath(options.CachePath, environment.ContentRootPath);

            return new DiskAudioCache(root, sp.GetRequiredService<ILogger<DiskAudioCache>>());
        });

        builder.Services.AddSingleton<CoalescingAudioSource>();

        AddRateLimiting(builder);
    }

    /// <summary>
    /// Registers the fixed window policy the controller runs under, and puts the middleware that
    /// enforces it into Umbraco's pipeline.
    /// </summary>
    /// <remarks>
    /// The framework limiter rather than a hand-rolled one, and a pipeline filter rather than a
    /// line in the site's Program.cs, so installing the package is still the whole install.
    ///
    /// The window is per caller as the server sees it. Behind a proxy or CDN that is the edge
    /// rather than the reader unless the site enables forwarded headers, so read
    /// <see cref="ReadAloudRateLimiterPolicy"/> and the README note before trusting the number.
    ///
    /// A site whose own Program.cs already calls UseRateLimiter ends up with the middleware twice,
    /// and each pass takes its own lease, which halves the effective limit here. Nothing detects
    /// that: a package cannot tell the difference between its own registration and the host's, and
    /// guessing would be worse than saying so.
    /// </remarks>
    private static void AddRateLimiting(IUmbracoBuilder builder)
    {
        builder.Services.AddRateLimiter(limiter =>
            limiter.AddPolicy<string, ReadAloudRateLimiterPolicy>(ReadAloudRateLimiting.PolicyName));

        // PostRouting, because the policy is attached to the endpoint by an attribute, so the
        // middleware has to run after routing has chosen the endpoint and before it is executed.
        //
        // The concrete filter rather than the IUmbracoPipelineFilter interface on purpose. Umbraco
        // 18 both adds OnPreMapEndpoints and OnPostMapEndpoints to that interface and drops the
        // default bodies 16 and 17 gave OnPreRouting and OnPostRouting, so a class implementing it
        // directly cannot compile against all three. This class exists on every major and its
        // extra members are simply left unset.
        builder.Services.Configure<UmbracoPipelineOptions>(options =>
            options.AddFilter(new UmbracoPipelineFilter(nameof(ReadAloudComposer))
            {
                PostRouting = app => app.UseRateLimiter(),
            }));
    }

    /// <summary>
    /// Resolves a configured cache path against the site root.
    /// </summary>
    /// <remarks>
    /// A relative path follows the site root rather than the process working directory, which
    /// differs between dotnet run, IIS and a test host. The default is relative, so this is the
    /// branch nearly every site takes.
    /// </remarks>
    internal static string ResolveCachePath(string configured, string contentRootPath) =>
        Path.IsPathRooted(configured) ? configured : Path.Combine(contentRootPath, configured);
}
