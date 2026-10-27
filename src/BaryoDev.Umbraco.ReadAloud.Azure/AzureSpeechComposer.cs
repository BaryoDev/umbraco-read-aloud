using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>
/// Registers the Azure engine, so installing the package plus its settings is the whole setup.
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

        builder.Services.AddSingleton<AzureSpeechEngine>();

        // Picked per resolution from Provider, not swapped unconditionally: with any other provider
        // the engine the core chose keeps serving, exactly as without this package. A singleton,
        // so this is decided once at startup: changing Provider needs a restart.
        builder.Services.Replace(ServiceDescriptor.Singleton<IReadAloudEngine>(sp =>
            string.Equals(
                sp.GetRequiredService<IOptionsMonitor<ReadAloudOptions>>().CurrentValue.Provider,
                ReadAloudOptions.AzureProvider,
                StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<AzureSpeechEngine>()
                : string.Equals(
                    sp.GetRequiredService<IOptionsMonitor<ReadAloudOptions>>().CurrentValue.Provider,
                    ReadAloudOptions.EdgeProvider,
                    StringComparison.OrdinalIgnoreCase)
                    ? sp.GetRequiredService<EdgeTtsEngine>()
                    : sp.GetRequiredService<BrowserVoiceEngine>()));
    }
}
