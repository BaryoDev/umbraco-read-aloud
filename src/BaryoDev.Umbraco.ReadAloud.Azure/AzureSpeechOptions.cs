using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>
/// Settings for the Azure AI Speech engine, under <c>BaryoDev:ReadAloud:Azure</c>.
/// </summary>
/// <remarks>
/// The key is read from configuration and nowhere else, so it can come from an environment
/// variable (<c>BaryoDev__ReadAloud__Azure__Key</c>) or user secrets and never has to be in a file
/// that goes to git. It is never logged, never put in an exception message and never part of a
/// cache key.
/// </remarks>
public class AzureSpeechOptions
{
    /// <summary>The configuration section this binds to.</summary>
    /// <remarks>
    /// <c>static readonly</c> for the same reason as <see cref="ReadAloudOptions.SectionName"/>.
    /// </remarks>
    public static readonly string SectionName = ReadAloudOptions.SectionName + ":Azure";

    /// <summary>The Azure region of the Speech resource, for example <c>southeastasia</c>.</summary>
    public string Region { get; set; } = "";

    /// <summary>A key of the Speech resource. Keep it out of files committed to git.</summary>
    public string Key { get; set; } = "";
}

/// <summary>
/// Tells the core that <c>Provider: Azure</c> has an engine behind it, and checks its settings.
/// </summary>
internal sealed class AzureSpeechProviderSettings : IReadAloudProviderSettings
{
    private readonly IOptionsMonitor<AzureSpeechOptions> _options;

    public AzureSpeechProviderSettings(IOptionsMonitor<AzureSpeechOptions> options)
    {
        _options = options;
    }

    public string Provider => ReadAloudOptions.AzureProvider;

    public IReadOnlyList<string> Validate() => Problems(_options.CurrentValue);

    /// <summary>
    /// Each message names the setting and never the value, because one of the two is a secret and
    /// startup failures end up in logs and error pages.
    /// </summary>
    internal static IReadOnlyList<string> Problems(AzureSpeechOptions options)
    {
        var problems = new List<string>();

        var region = options.Region?.Trim() ?? "";
        var key = options.Key?.Trim() ?? "";

        if (region.Length == 0)
        {
            problems.Add(
                $"{AzureSpeechOptions.SectionName}:Region is not set, and Provider is "
                + $"\"{ReadAloudOptions.AzureProvider}\". Set it to the region of your Speech resource, "
                + "for example \"southeastasia\".");
        }
        else if (!IsRegionName(region))
        {
            // The region becomes part of a host name, so anything but a plain region id is refused.
            problems.Add(
                $"{AzureSpeechOptions.SectionName}:Region is not a region id. Use lowercase letters "
                + "and digits, for example \"southeastasia\".");
        }

        if (key.Length == 0)
        {
            problems.Add(
                $"{AzureSpeechOptions.SectionName}:Key is not set, and Provider is "
                + $"\"{ReadAloudOptions.AzureProvider}\". Set it from an environment variable "
                + "(BaryoDev__ReadAloud__Azure__Key) or user secrets, not a file committed to git.");
        }

        return problems;
    }

    private static bool IsRegionName(string region) =>
        region.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
