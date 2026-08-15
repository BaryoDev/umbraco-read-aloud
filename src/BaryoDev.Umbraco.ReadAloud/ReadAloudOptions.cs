namespace BaryoDev.Umbraco.ReadAloud;

/// <summary>
/// Everything a site can configure. Every value has a working default, so a site that configures
/// nothing still gets a working read-aloud button.
/// </summary>
public class ReadAloudOptions
{
    public const string SectionName = "BaryoDev:ReadAloud";

    public bool Enabled { get; set; } = true;

    /// <summary>Document type aliases this applies to. Empty means all of them.</summary>
    public List<string> DocumentTypes { get; set; } = [];

    /// <summary>The property read aloud, unless a page overrides it.</summary>
    public string PropertyAlias { get; set; } = "bodyText";

    public string DefaultVoice { get; set; } = "en-GB-SoniaNeural";

    /// <summary>Voices a caller may request. Empty means only DefaultVoice is allowed.</summary>
    public List<string> AllowedVoices { get; set; } = [];

    /// <summary>Caps how much text is sent in one request.</summary>
    public int MaxChars { get; set; } = 8000;

    public string CachePath { get; set; } = "App_Data/BaryoDev/ReadAloud";

    /// <summary>Requests per minute per IP, since the endpoint is anonymous.</summary>
    public int RateLimitPerMinute { get; set; } = 20;

    /// <summary>"Edge" (default, free, unsupported) or "AzureSpeech" (paid, contracted).</summary>
    public string Provider { get; set; } = "Edge";
}
