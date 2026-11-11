using BaryoDev.Umbraco.ReadAloud.Engine;
using BaryoDev.Umbraco.ReadAloud.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>
/// What the Read Aloud dashboard does with the Azure credentials: report, test, save and remove.
/// </summary>
/// <remarks>
/// Every message here is written in this class. None is copied from Azure or from the SDK, and none
/// contains the key, so nothing a response or a log line carries can leak it.
/// </remarks>
internal sealed class AzureSpeechAdmin : IReadAloudAzureAdmin
{
    private const int MaxKeyLength = 512;

    private readonly AzureCredentialResolver _resolver;
    private readonly AzureCredentialStore _store;
    private readonly SwitchingReadAloudEngine _engine;
    private readonly IAzureVoiceClient _voiceClient;
    private readonly IAzureVoiceList _voiceList;
    private readonly IOptionsMonitor<ReadAloudOptions> _core;
    private readonly ILogger<AzureSpeechAdmin> _logger;

    public AzureSpeechAdmin(
        AzureCredentialResolver resolver,
        AzureCredentialStore store,
        SwitchingReadAloudEngine engine,
        IAzureVoiceClient voiceClient,
        IAzureVoiceList voiceList,
        IOptionsMonitor<ReadAloudOptions> core,
        ILogger<AzureSpeechAdmin> logger)
    {
        _resolver = resolver;
        _store = store;
        _engine = engine;
        _voiceClient = voiceClient;
        _voiceList = voiceList;
        _core = core;
        _logger = logger;
    }

    public AzureStatusModel GetStatus()
    {
        var r = _resolver.Resolve();

        var voice = (_engine.Active().Provider ?? ReadAloudOptions.BrowserProvider).ToLowerInvariant();

        return new AzureStatusModel(
            Installed: true,
            KeySaved: r.Credential is not null,
            Source: r.Source switch
            {
                CredentialSource.Config => "config",
                CredentialSource.Dashboard => "dashboard",
                _ => "none",
            },
            Region: r.Region,
            Unreadable: r.Unreadable,
            LockedByConfig: r.LockedByConfig,
            Voice: voice,
            ConfigKeyIgnored: r.ConfigKeyIgnored);
    }

    public async Task<AzureTestModel> TestAsync(string? region, string? key, CancellationToken ct)
    {
        var inUse = _resolver.Current;

        var useRegion = Clean(region) is { Length: > 0 } typedRegion ? typedRegion : inUse?.Region ?? "";
        var useKey = Clean(key) is { Length: > 0 } typedKey ? typedKey : inUse?.Key ?? "";

        if (useKey.Length == 0)
        {
            return new AzureTestModel(false, "missing", "Enter a key to test, or save one first.");
        }

        var invalid = Invalid(useRegion, useKey);
        if (invalid is not null) return new AzureTestModel(false, "invalid", invalid);

        var result = await _voiceClient.FetchAsync(useRegion, useKey, ct);

        // A green test clears a failure kept from earlier, so the voice list is not "unavailable"
        // for minutes after the key is shown to work.
        if (result.Status == VoiceFetchStatus.Ok) _voiceList.Evict(useRegion, useKey);

        return result.Status switch
        {
            VoiceFetchStatus.Ok => new AzureTestModel(
                true, "ok", $"Azure accepted the key for {useRegion}. {result.Voices.Count} voices available."),
            VoiceFetchStatus.Rejected => new AzureTestModel(
                false, "rejected",
                $"Azure refused this key for {useRegion}. Check the key and that the region matches the resource's Location."),
            VoiceFetchStatus.Network => new AzureTestModel(false, "network", "Could not reach Azure."),
            VoiceFetchStatus.Invalid => new AzureTestModel(false, "invalid", "The key does not look like an Azure Speech key."),
            _ => new AzureTestModel(false, "failed", $"Azure answered with status {result.Code}."),
        };
    }

    public async Task<AzureVoiceStatusModel> GetVoicesAsync(CancellationToken ct)
    {
        var configured = AzureVoiceList.ConfiguredVoices(_core.CurrentValue);

        var snapshot = await _voiceList.GetAsync(ct);
        if (snapshot is null)
        {
            return new AzureVoiceStatusModel(false, 0, null, configured.Select(v => new AzureConfiguredVoice(v, null)).ToList());
        }

        return new AzureVoiceStatusModel(
            snapshot.Voices is not null,
            snapshot.Voices?.Count ?? 0,
            snapshot.Region,
            configured.Select(v => new AzureConfiguredVoice(v, snapshot.Voices?.Contains(v))).ToList());
    }

    public AzureSaveModel Save(string? region, string? key, string savedBy)
    {
        if (_resolver.Resolve().LockedByConfig) return Locked();

        var stored = _store.Current();
        var newRegion = Clean(region);
        var newKey = Clean(key);

        // A blank key keeps the saved one, so the region can change without typing the key again.
        if (newKey.Length == 0)
        {
            if (stored.State != StoredState.Readable || stored.Key is null)
            {
                return new AzureSaveModel(false, "missing", "Enter a key.");
            }

            newKey = stored.Key;
        }

        if (newRegion.Length == 0) newRegion = stored.Region ?? "";

        var invalid = Invalid(newRegion, newKey);
        if (invalid is not null) return new AzureSaveModel(false, "invalid", invalid);

        _store.Save(newRegion, newKey, savedBy);
        _voiceList.Evict(newRegion, newKey);
        _logger.LogInformation("The Azure Speech key was saved from the backoffice by {User}.", savedBy);

        return new AzureSaveModel(true, "ok", "Saved. Readers get the Azure voice from the next request.");
    }

    public AzureSaveModel Remove(string removedBy)
    {
        if (_resolver.Resolve().LockedByConfig) return Locked();

        var before = _resolver.Current;
        _store.Clear();
        if (before is not null) _voiceList.Evict(before.Region, before.Key);
        _logger.LogInformation("The Azure Speech key was removed from the backoffice by {User}.", removedBy);

        return new AzureSaveModel(true, "ok", "Removed. Readers get the browser voice from the next request.");
    }

    private static AzureSaveModel Locked() => new(
        false,
        "locked",
        "The key is set by configuration (BaryoDev:ReadAloud:Azure). Change it there.");

    private static string Clean(string? value) => value?.Trim() ?? "";

    /// <summary>A message naming the problem, or null. Never repeats the key.</summary>
    private static string? Invalid(string region, string key)
    {
        if (region.Length == 0) return "Enter the region of your Speech resource, for example southeastasia.";

        if (!AzureSpeechProviderSettings.IsRegionName(region))
        {
            return "The region is not a region id. Use lowercase letters and digits, for example southeastasia.";
        }

        if (key.Length > MaxKeyLength || key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return "The key does not look like an Azure Speech key.";
        }

        return null;
    }
}
