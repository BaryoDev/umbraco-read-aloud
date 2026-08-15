using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

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

            // A relative path is resolved against the site root rather than the process working
            // directory, which differs between dotnet run, IIS and a test host.
            var root = Path.IsPathRooted(options.CachePath)
                ? options.CachePath
                : Path.Combine(environment.ContentRootPath, options.CachePath);

            return new DiskAudioCache(root, sp.GetRequiredService<ILogger<DiskAudioCache>>());
        });

        builder.Services.AddSingleton<CoalescingAudioSource>();
    }
}
