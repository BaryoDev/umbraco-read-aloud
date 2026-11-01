using BaryoDev.Umbraco.ReadAloud.Azure;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// A fact that is skipped unless the Azure key is in the environment. xUnit 2.9.3 cannot skip at
/// run time, so the skip reason is set when the attribute is constructed.
/// </summary>
public sealed class AzureLiveFactAttribute : FactAttribute
{
    public const string KeyVariable = "BaryoDev__ReadAloud__Azure__Key";
    public const string RegionVariable = "BaryoDev__ReadAloud__Azure__Region";

    public AzureLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyVariable)))
        {
            Skip = $"Calls Azure AI Speech. Set {KeyVariable} to run it.";
        }
    }
}

public class AzureSpeechEngineLiveTests
{
    [AzureLiveFact]
    [Trait("Category", "Live")]
    public async Task Live_synthesis_returns_mp3_and_word_timings()
    {
        // The key is read from the environment into the options and goes nowhere else: no output,
        // no assertion message, no log.
        var options = new AzureSpeechOptions
        {
            Key = Environment.GetEnvironmentVariable(AzureLiveFactAttribute.KeyVariable)!,
            Region = Environment.GetEnvironmentVariable(AzureLiveFactAttribute.RegionVariable) is { Length: > 0 } r
                ? r
                : "southeastasia",
        };

        var engine = new AzureSpeechEngine(
            NullLogger<AzureSpeechEngine>.Instance,
            new Fixed(new AzureCredential(options.Region, options.Key, CredentialSource.Config)));

        var result = await engine.SynthesizeAsync(new SynthesisRequest
        {
            Text = "There is a listen button on this article.",
            Voice = "en-PH-RosaNeural",
        });

        result.ContentType.ShouldBe("audio/mpeg");
        result.Audio.Length.ShouldBeGreaterThan(1000);
        result.Boundaries.ShouldNotBeEmpty();
        result.Boundaries[0].DurationMs.ShouldBeGreaterThan(0);
    }

    private sealed class Fixed(AzureCredential value) : IAzureCredentialSource
    {
        public AzureCredential? Current { get; } = value;
    }
}
