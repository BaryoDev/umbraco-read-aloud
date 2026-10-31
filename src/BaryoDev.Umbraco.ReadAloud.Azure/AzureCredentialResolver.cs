using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

internal enum CredentialSource
{
    None,
    Config,
    Dashboard,
}

/// <summary>The region and key to synthesize with right now, and where they came from.</summary>
internal sealed record AzureCredential(string Region, string Key, CredentialSource Source);

/// <summary>What the engine and the dashboard need to know about the credentials in force.</summary>
internal sealed record AzureResolution(
    AzureCredential? Credential,
    CredentialSource Source,
    string? Region,
    bool Unreadable,
    bool LockedByConfig);

/// <summary>Anything that can say which credentials to use. A seam so tests need no store.</summary>
internal interface IAzureCredentialSource
{
    AzureCredential? Current { get; }
}

/// <summary>
/// Works out the Azure credentials per call, so a key saved or removed in the backoffice is used
/// from the next request.
/// </summary>
/// <remarks>
/// Configuration wins: <c>Provider</c> set to Azure with a key under
/// <c>BaryoDev:ReadAloud:Azure</c>. Otherwise the stored key is used. Nothing anywhere resolves
/// to no credential, which means the browser voice.
/// </remarks>
internal sealed class AzureCredentialResolver : IAzureCredentialSource
{
    private readonly IOptionsMonitor<ReadAloudOptions> _core;
    private readonly IOptionsMonitor<AzureSpeechOptions> _azure;
    private readonly AzureCredentialStore _store;

    public AzureCredentialResolver(
        IOptionsMonitor<ReadAloudOptions> core,
        IOptionsMonitor<AzureSpeechOptions> azure,
        AzureCredentialStore store)
    {
        _core = core;
        _azure = azure;
        _store = store;
    }

    public AzureCredential? Current => Resolve().Credential;

    public AzureResolution Resolve()
    {
        var azure = _azure.CurrentValue;
        var key = azure.Key?.Trim() ?? "";

        if (key.Length > 0 && IsAzureProvider(_core.CurrentValue.Provider))
        {
            var region = azure.Region?.Trim() ?? "";
            return new AzureResolution(
                new AzureCredential(region, key, CredentialSource.Config),
                CredentialSource.Config,
                region,
                Unreadable: false,
                LockedByConfig: true);
        }

        var stored = _store.Current();

        return stored.State switch
        {
            StoredState.Readable => new AzureResolution(
                new AzureCredential(stored.Region, stored.Key!, CredentialSource.Dashboard),
                CredentialSource.Dashboard,
                stored.Region,
                Unreadable: false,
                LockedByConfig: false),
            StoredState.Unreadable => new AzureResolution(
                null, CredentialSource.None, stored.Region, Unreadable: true, LockedByConfig: false),
            _ => new AzureResolution(null, CredentialSource.None, null, Unreadable: false, LockedByConfig: false),
        };
    }

    internal static bool IsAzureProvider(string? provider) =>
        string.Equals(provider, ReadAloudOptions.AzureProvider, StringComparison.OrdinalIgnoreCase);
}
