using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>Where the stored record lives. A seam so tests need no database.</summary>
internal interface ICredentialRecords
{
    string? Read();

    void Write(string value);
}

/// <summary>Umbraco's key-value table, so the package adds no tables of its own.</summary>
internal sealed class KeyValueCredentialRecords(IKeyValueService keyValues) : ICredentialRecords
{
    internal const string Key = "BaryoDev.ReadAloud.AzureSpeech";

    public string? Read() => keyValues.GetValue(Key);

    public void Write(string value) => keyValues.SetValue(Key, value);
}

internal enum StoredState
{
    /// <summary>Nothing is stored.</summary>
    None,

    /// <summary>A key is stored and was decrypted.</summary>
    Readable,

    /// <summary>A record is stored but cannot be decrypted, for example because the key ring is gone.</summary>
    Unreadable,
}

/// <summary>What is stored. <see cref="Key"/> is set only when <see cref="State"/> is Readable.</summary>
internal sealed record StoredCredential(StoredState State, string Region, string? Key)
{
    public static readonly StoredCredential Empty = new(StoredState.None, "", null);
}

/// <summary>
/// The Azure key saved from the backoffice. The key is encrypted with ASP.NET Data Protection using
/// a key ring of its own, kept under the site's data folder, so the host's own Data Protection
/// setup is neither used nor touched and a new container keeps the ring if that folder persists.
/// The region is not a secret and is stored as it is.
/// </summary>
/// <remarks>
/// Read on every request through a short cache, which Save and Remove clear at once. The cache
/// bounds the database reads, and its expiry is how another server in a load balanced site
/// notices a change. Nothing here throws on a read: a visitor's request must never fail because
/// of the settings store.
/// </remarks>
internal sealed class AzureCredentialStore
{
    internal const string Purpose = "BaryoDev.ReadAloud.Azure.Key";

    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    private readonly ICredentialRecords _records;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<AzureCredentialStore> _logger;
    private readonly object _gate = new();

    private (StoredCredential Value, DateTimeOffset Expires)? _cached;
    private bool _warnedUnreadable;

    public AzureCredentialStore(
        ICredentialRecords records,
        IDataProtectionProvider protection,
        TimeProvider time,
        ILogger<AzureCredentialStore> logger)
    {
        _records = records;
        _protector = protection.CreateProtector(Purpose);
        _time = time;
        _logger = logger;
    }

    /// <summary>The stored credential, from the cache when it is fresh. Never throws.</summary>
    public StoredCredential Current()
    {
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (_cached is { } cached && cached.Expires > now) return cached.Value;
        }

        var value = Load();

        lock (_gate)
        {
            _cached = (value, now + CacheFor);
        }

        return value;
    }

    /// <summary>
    /// Whether a record exists, without decrypting it. Null when that cannot be told, for example
    /// before the database is ready. Used by startup validation only.
    /// </summary>
    public bool? HasRecord()
    {
        try
        {
            return Parse(_records.Read()) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not check for a saved Azure Speech key at startup ({ExceptionType}).",
                ex.GetType().Name);
            return null;
        }
    }

    public void Save(string region, string key, string savedBy)
    {
        _records.Write(JsonSerializer.Serialize(new Record
        {
            Region = region,
            ProtectedKey = _protector.Protect(key),
            SavedBy = savedBy,
            SavedAt = _time.GetUtcNow(),
        }));

        Forget();
    }

    public void Clear()
    {
        _records.Write("");
        Forget();
    }

    private void Forget()
    {
        lock (_gate)
        {
            _cached = null;
            _warnedUnreadable = false;
        }
    }

    private StoredCredential Load()
    {
        Record? record;
        try
        {
            record = Parse(_records.Read());
        }
        catch (JsonException)
        {
            return Unreadable("");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not read the saved Azure Speech key ({ExceptionType}). Using the browser voice.",
                ex.GetType().Name);
            return StoredCredential.Empty;
        }

        if (record is null) return StoredCredential.Empty;

        try
        {
            return new StoredCredential(StoredState.Readable, record.Region, _protector.Unprotect(record.ProtectedKey));
        }
        catch (Exception)
        {
            // Not logged beyond this: the exception text can name key ids, and the value is not
            // ours to print. The ring being gone is the usual cause.
            return Unreadable(record.Region);
        }
    }

    private StoredCredential Unreadable(string region)
    {
        bool warn;
        lock (_gate)
        {
            warn = !_warnedUnreadable;
            _warnedUnreadable = true;
        }

        if (warn)
        {
            _logger.LogWarning(
                "The Azure Speech key saved in the backoffice can no longer be decrypted, so readers "
                + "get the browser voice. The Data Protection key ring under the site's data folder "
                + "was probably lost. Enter the key again on the Read Aloud dashboard.");
        }

        return new StoredCredential(StoredState.Unreadable, region, null);
    }

    private static Record? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        var record = JsonSerializer.Deserialize<Record>(json);
        return record is null || string.IsNullOrEmpty(record.ProtectedKey) ? null : record;
    }

    private sealed class Record
    {
        public string Region { get; set; } = "";
        public string ProtectedKey { get; set; } = "";
        public string SavedBy { get; set; } = "";
        public DateTimeOffset SavedAt { get; set; }
    }
}
