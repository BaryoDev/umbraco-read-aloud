using BaryoDev.Umbraco.ReadAloud.Engine;
using BaryoDev.Umbraco.ReadAloud.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>
/// Registers the Azure engine, so installing the package is the whole setup. The key then comes
/// from the backoffice, or from configuration when a site prefers that.
/// </summary>
[ComposeAfter(typeof(ReadAloudComposer))]
public class AzureSpeechComposer : IComposer
{
    /// <inheritdoc />
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddOptions<AzureSpeechOptions>()
            .Bind(builder.Config.GetSection(AzureSpeechOptions.SectionName));

        // The core validates Provider on start and asks this registration about the Azure settings.
        // Nothing is checked unless Provider is Azure, so installing the package changes nothing for
        // a site that has not opted in.
        builder.Services.AddSingleton<IReadAloudProviderSettings, AzureSpeechProviderSettings>();

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<ICredentialRecords, KeyValueCredentialRecords>();
        builder.Services.AddSingleton<IAzureProbe, AzureProbe>();

        // A key ring of its own, so the saved key does not depend on how the host protects data.
        // The folder comes from raw configuration, not from IOptions: startup validation asks this
        // store a question while ReadAloudOptions is still being built, and reading those options
        // from inside the store would re-enter them.
        var keysPath = builder.Config[ReadAloudOptions.SectionName + ":KeysPath"];

        builder.Services.AddSingleton(sp =>
        {
            var folder = KeyFolder(keysPath, sp.GetRequiredService<IWebHostEnvironment>());

            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(folder),
                options => options.SetApplicationName("BaryoDev.ReadAloud"));

            return new AzureCredentialStore(
                sp.GetRequiredService<ICredentialRecords>(),
                provider,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<AzureCredentialStore>>());
        });

        builder.Services.AddSingleton<AzureCredentialResolver>();
        builder.Services.AddSingleton<IAzureCredentialSource>(sp => sp.GetRequiredService<AzureCredentialResolver>());
        builder.Services.AddSingleton<AzureSpeechEngine>();
        builder.Services.AddSingleton<SwitchingReadAloudEngine>();
        builder.Services.AddSingleton<IReadAloudAzureAdmin, AzureSpeechAdmin>();

        // The engine is chosen on every call, so Provider and the saved key can change with no restart.
        builder.Services.Replace(ServiceDescriptor.Singleton<IReadAloudEngine>(
            sp => sp.GetRequiredService<SwitchingReadAloudEngine>()));
    }

    internal static string KeyFolder(string? keysPath, IWebHostEnvironment environment) =>
        string.IsNullOrWhiteSpace(keysPath)
            ? Path.Combine(environment.ContentRootPath, "umbraco", "Data", "DataProtection-Keys", "ReadAloud")
            : Path.GetFullPath(keysPath, environment.ContentRootPath);
}
