using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud;

/// <summary>
/// Everything a site can configure. Every value has a working default, so a site that configures
/// nothing still gets a working read-aloud button.
/// </summary>
public class ReadAloudOptions
{
    /// <summary>The configuration section this binds to.</summary>
    /// <remarks>
    /// <c>static readonly</c> rather than <c>const</c>, and the difference is not style. C# bakes a
    /// const into the assembly that reads it, so a consumer who wrote
    /// <c>GetSection(ReadAloudOptions.SectionName)</c> holds their own copy of the literal. If this
    /// ever moved, they would go on binding a section that no longer exists, get every default, and
    /// see no error until someone noticed the settings were being ignored. A static readonly field
    /// is read at run time, so an upgrade reaches them.
    /// </remarks>
    public static readonly string SectionName = "BaryoDev:ReadAloud";

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

    /// <summary>
    /// Where the private key ring lives that encrypts a key saved in the backoffice. Empty means
    /// <c>umbraco/Data/DataProtection-Keys/ReadAloud</c> under the site root. Keep this folder
    /// across deployments, or a saved key can no longer be read.
    /// </summary>
    public string KeysPath { get; set; } = "";

    /// <summary>Requests per minute per IP, since the endpoint is anonymous.</summary>
    public int RateLimitPerMinute { get; set; } = 20;

    /// <summary>How many articles may be synthesized at once, across the whole site.</summary>
    /// <remarks>
    /// The rate limit counts requests arriving. This counts work still running, which is a
    /// different quantity: one synthesis of a long article is minutes of an open WebSocket and a
    /// buffer holding the whole recording, and it deliberately keeps running after the reader who
    /// asked for it has gone, so that the cache is filled for whoever asks next. Without a ceiling,
    /// accumulated work grows with arrival rate times duration, and every published node is a
    /// separate key that anyone can ask for, since the key is in the page markup by design.
    ///
    /// Past the ceiling a request is refused rather than queued, and the reader gets the browser's
    /// own voice instead of a wait with no end in sight. Queueing would move the same unbounded
    /// growth into the queue.
    ///
    /// Four is deliberately modest. This is a burst of connections to an endpoint that Microsoft
    /// does not support and has no obligation to keep answering, so the cost of being slow to fill
    /// a cold cache is much lower than the cost of looking like abuse.
    /// </remarks>
    public int MaxConcurrentSynthesis { get; set; } = 4;

    /// <summary>
    /// Which voice speaks: <see cref="BrowserProvider"/> (the default), <see cref="AzureProvider"/>
    /// or the deprecated <see cref="EdgeProvider"/>.
    /// </summary>
    /// <remarks>
    /// <c>Browser</c> does no server synthesis. The reader's browser speaks the text with its own
    /// built-in voice. <c>Azure</c> needs the <c>BaryoDev.Umbraco.ReadAloud.Azure</c> package, which
    /// registers the engine and its settings. This setting exists to refuse a value that would
    /// otherwise look configured and do nothing: a name with no provider package behind it, or a
    /// provider with a missing setting, stops the site at startup.
    ///
    /// With the Azure package installed, a key saved in the backoffice turns on the Azure voice
    /// when this is left at <c>Browser</c>, and removing the key turns it off again, with no
    /// restart. Without the Azure package the engine is chosen once, when the site starts.
    /// </remarks>
    public string Provider { get; set; } = BrowserProvider;

    /// <summary>The browser's own built-in voice. No server synthesis happens. The default.</summary>
    /// <remarks>Same reason as <see cref="EdgeProvider"/> for being <c>static readonly</c>.</remarks>
    public static readonly string BrowserProvider = "Browser";

    /// <summary>The unofficial endpoint Microsoft Edge itself uses.</summary>
    /// <remarks>
    /// Deprecated, and it will be removed in a later release. Microsoft now requires a token on
    /// this endpoint that this package does not generate, so requests are rejected (see issue 18).
    /// Use <see cref="AzureProvider"/> instead. Selecting it logs a warning at startup.
    ///
    /// <c>static readonly</c> for the same reason as <see cref="SectionName"/>: a const would be
    /// inlined into any site that compared against it, so this value could never be changed for
    /// anyone who had already built.
    /// </remarks>
    public static readonly string EdgeProvider = "Edge";

    /// <summary>Azure AI Speech, the supported service. Needs the Azure package.</summary>
    public static readonly string AzureProvider = "Azure";
}

/// <summary>
/// Stops the boot on a configured value that would otherwise be silently inert or silently fatal.
/// </summary>
/// <remarks>
/// Registered with <c>ValidateOnStart</c>, so a mistake here is a failed startup with a message
/// rather than a running site whose configuration is inert. Silently continuing is the one answer
/// that should not ship: the setting's whole purpose to a site owner is choosing where the audio
/// comes from, and a site owner who believes they moved off the unofficial endpoint and has not is
/// worse off than one who was stopped and told.
/// </remarks>
internal sealed class ReadAloudOptionsValidation : IValidateOptions<ReadAloudOptions>
{
    private readonly IReadOnlyList<IReadAloudProviderSettings> _providers;
    private readonly ILogger<ReadAloudOptionsValidation> _logger;

    public ReadAloudOptionsValidation(
        IEnumerable<IReadAloudProviderSettings> providers,
        ILogger<ReadAloudOptionsValidation> logger)
    {
        _providers = providers.ToList();
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, ReadAloudOptions options)
    {
        if (options.MaxConcurrentSynthesis < 1)
        {
            return ValidateOptionsResult.Fail(
                $"{ReadAloudOptions.SectionName}:MaxConcurrentSynthesis is "
                + $"{options.MaxConcurrentSynthesis}, which refuses every synthesis there is. Set it "
                + "to at least 1, or remove it to take the default.");
        }

        // Without case, because this is hand-typed into a configuration file, the same reason the
        // controller compares document type aliases without case.
        if (string.Equals(options.Provider, ReadAloudOptions.BrowserProvider, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Success;
        }

        if (string.Equals(options.Provider, ReadAloudOptions.EdgeProvider, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "{Section}:Provider is \"Edge\", which is deprecated and will be removed in a later "
                + "release. The Microsoft endpoint now rejects these requests (see issue 18), so "
                + "readers get the browser's own voice. Use \"Azure\" with the "
                + "BaryoDev.Umbraco.ReadAloud.Azure package, or remove the setting.",
                ReadAloudOptions.SectionName);

            return ValidateOptionsResult.Success;
        }

        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Provider, options.Provider, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            var hint = string.Equals(options.Provider, ReadAloudOptions.AzureProvider, StringComparison.OrdinalIgnoreCase)
                ? "Install the BaryoDev.Umbraco.ReadAloud.Azure package to use Azure. "
                : "";

            return ValidateOptionsResult.Fail(
                $"{ReadAloudOptions.SectionName}:Provider is set to \"{options.Provider}\", and no "
                + $"provider with that name is registered. The supported values are \"{ReadAloudOptions.BrowserProvider}\", "
                + $"\"{ReadAloudOptions.AzureProvider}\" and \"{ReadAloudOptions.EdgeProvider}\" (deprecated). {hint}Remove the setting to take the "
                + "default, or register your own IReadAloudEngine to synthesize somewhere else.");
        }

        // The provider names the setting that is wrong. It is never given the chance to print one.
        var problems = provider.Validate();

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems);
    }
}
