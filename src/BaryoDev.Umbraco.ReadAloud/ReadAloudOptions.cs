using Microsoft.Extensions.Options;

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

    /// <summary>The only value this version implements is <see cref="EdgeProvider"/>.</summary>
    /// <remarks>
    /// The synthesis engine is a registered service (<c>IReadAloudEngine</c>), so a site that needs
    /// a different one replaces the registration rather than naming it here. This setting exists to
    /// refuse a value that would otherwise look configured and do nothing: anything but
    /// <see cref="EdgeProvider"/> stops the site at startup rather than quietly leaving every
    /// request on the unofficial Edge endpoint.
    /// </remarks>
    public string Provider { get; set; } = EdgeProvider;

    /// <summary>The free, unofficial, unsupported endpoint Microsoft Edge itself uses.</summary>
    public const string EdgeProvider = "Edge";
}

/// <summary>
/// Stops the boot when <see cref="ReadAloudOptions.Provider"/> names something that is not built.
/// </summary>
/// <remarks>
/// Registered with <c>ValidateOnStart</c>, so a mistake here is a failed startup with a message
/// rather than a running site whose configuration is inert. Silently continuing is the one answer
/// that should not ship: the setting's whole purpose to a site owner is choosing where the audio
/// comes from, and a site owner who believes they moved off the unofficial endpoint and has not is
/// worse off than one who was stopped and told.
/// </remarks>
internal sealed class ReadAloudProviderValidation : IValidateOptions<ReadAloudOptions>
{
    public ValidateOptionsResult Validate(string? name, ReadAloudOptions options)
    {
        // Without case, because this is hand-typed into a configuration file, the same reason the
        // controller compares document type aliases without case.
        if (string.Equals(options.Provider, ReadAloudOptions.EdgeProvider, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"{ReadAloudOptions.SectionName}:Provider is set to \"{options.Provider}\", which this "
            + $"version does not implement. The only supported value is \"{ReadAloudOptions.EdgeProvider}\". "
            + "Azure Speech is not implemented in v1: there is no Azure engine in this package and no "
            + "credentials are read anywhere. Remove the setting to take the default, or register your "
            + "own IReadAloudEngine to synthesize somewhere else.");
    }
}
