using BaryoDev.Umbraco.ReadAloud.Engine;
using BaryoDev.Umbraco.ReadAloud.Settings;
using Microsoft.Extensions.Logging;

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
    private readonly IAzureProbe _probe;
    private readonly ILogger<AzureSpeechAdmin> _logger;

    public AzureSpeechAdmin(
        AzureCredentialResolver resolver,
        AzureCredentialStore store,
        SwitchingReadAloudEngine engine,
        IAzureProbe probe,
        ILogger<AzureSpeechAdmin> logger)
    {
        _resolver = resolver;
        _store = store;
        _engine = engine;
        _probe = probe;
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
            Voice: voice);
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

        var outcome = await _probe.RunAsync(useRegion, useKey, ct);

        return outcome switch
        {
            ProbeOutcome.Ok => new AzureTestModel(true, "ok", "Azure Speech accepted the key and returned audio."),
            ProbeOutcome.Rejected => new AzureTestModel(
                false, "rejected",
                "Azure Speech rejected the key. Check the key, and that the region is the one the Speech resource was created in."),
            ProbeOutcome.Forbidden => new AzureTestModel(
                false, "rejected", "Azure Speech refused this key. Check that the resource is active and has access."),
            ProbeOutcome.Quota => new AzureTestModel(
                false, "quota", "Azure Speech is rate limiting or out of quota for this resource."),
            ProbeOutcome.Network => new AzureTestModel(
                false, "network",
                "Could not reach Azure Speech. Check the region name and that the server can make outbound HTTPS calls."),
            _ => new AzureTestModel(false, "failed", "The test synthesis failed."),
        };
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

        if (newRegion.Length == 0) newRegion = stored.Region;

        var invalid = Invalid(newRegion, newKey);
        if (invalid is not null) return new AzureSaveModel(false, "invalid", invalid);

        _store.Save(newRegion, newKey, savedBy);
        _logger.LogInformation("The Azure Speech key was saved from the backoffice by {User}.", savedBy);

        return new AzureSaveModel(true, "ok", "Saved. Readers get the Azure voice from the next request.");
    }

    public AzureSaveModel Remove(string removedBy)
    {
        if (_resolver.Resolve().LockedByConfig) return Locked();

        _store.Clear();
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
