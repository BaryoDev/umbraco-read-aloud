using System.Threading.RateLimiting;
using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        builder.Services.AddOptions<ReadAloudOptions>()
            .Bind(builder.Config.GetSection(ReadAloudOptions.SectionName));

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
    /// The partition key is the caller's IP held in memory for the length of one window, and
    /// nothing more: it is never written down, never logged and never leaves the process. This is
    /// the only place in the package that touches anything about a listener at all.
    /// </remarks>
    private static void AddRateLimiting(IUmbracoBuilder builder)
    {
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(ReadAloudRateLimiting.PolicyName, context =>
            {
                var perMinute = context.RequestServices
                    .GetRequiredService<IOptionsMonitor<ReadAloudOptions>>()
                    .CurrentValue.RateLimitPerMinute;

                // Zero or less turns it off, for a site behind its own gateway that would rather
                // rate limit there than here.
                if (perMinute <= 0) return RateLimitPartition.GetNoLimiter("unlimited");

                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = perMinute,
                        Window = TimeSpan.FromMinutes(1),

                        // Refused outright rather than queued. A reader wants to know now that the
                        // button will not work, not to have the page hang while a queue drains.
                        QueueLimit = 0,
                    });
            });
        });

        // PostRouting, because the policy is attached to the endpoint by an attribute, so the
        // middleware has to run after routing has chosen the endpoint and before it is executed.
        //
        // The concrete filter rather than the IUmbracoPipelineFilter interface on purpose:
        // Umbraco 18 added OnPreMapEndpoints and OnPostMapEndpoints to that interface, so a class
        // implementing it directly cannot compile against 16, 17 and 18 alike. This class exists
        // on all three and its extra members are simply left unset.
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
