using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>
/// The engine behind <see cref="IReadAloudEngine"/> once the Azure package is installed. It picks
/// the real engine on every call, so a key saved or removed in the backoffice changes the voice
/// from the next request, with no restart.
/// </summary>
/// <remarks>
/// Edge stays whatever configuration says. With Provider Azure or Browser, Azure runs when a key
/// resolves, from configuration or from the backoffice, and the browser voice runs when none does.
/// Any other provider name keeps the browser voice, as before. The cache key
/// follows the engine that answers, never the key, so replacing the key does not invalidate any
/// audio already made.
/// </remarks>
internal sealed class SwitchingReadAloudEngine : IReadAloudEngine
{
    private readonly IOptionsMonitor<ReadAloudOptions> _core;
    private readonly AzureCredentialResolver _resolver;
    private readonly AzureSpeechEngine _azure;
    private readonly EdgeTtsEngine _edge;
    private readonly BrowserVoiceEngine _browser;

    public SwitchingReadAloudEngine(
        IOptionsMonitor<ReadAloudOptions> core,
        AzureCredentialResolver resolver,
        AzureSpeechEngine azure,
        EdgeTtsEngine edge,
        BrowserVoiceEngine browser)
    {
        _core = core;
        _resolver = resolver;
        _azure = azure;
        _edge = edge;
        _browser = browser;
    }

    public string? Provider => Active().Provider;

    public bool SynthesizesOnServer => Active().SynthesizesOnServer;

    public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default) =>
        Active().SynthesizeAsync(request, ct);

    internal IReadAloudEngine Active()
    {
        var provider = _core.CurrentValue.Provider;

        if (string.Equals(provider, ReadAloudOptions.EdgeProvider, StringComparison.OrdinalIgnoreCase))
        {
            return _edge;
        }

        var azureAllowed = AzureCredentialResolver.IsAzureProvider(provider)
            || string.Equals(provider, ReadAloudOptions.BrowserProvider, StringComparison.OrdinalIgnoreCase);

        return azureAllowed && _resolver.Current is not null ? _azure : _browser;
    }
}
