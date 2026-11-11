using BaryoDev.Umbraco.ReadAloud.Azure;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.CognitiveServices.Speech;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class AzureWordBoundaryTests
{
    [Fact]
    public void Ticks_become_milliseconds()
    {
        // The SDK reports 100-nanosecond ticks. 12,500,000 ticks is 1.25 seconds.
        var boundary = AzureWordBoundaries.ToBoundary("Hello", 12_500_000UL, TimeSpan.FromMilliseconds(350));

        boundary.Text.ShouldBe("Hello");
        boundary.OffsetMs.ShouldBe(1250);
        boundary.DurationMs.ShouldBe(350);
    }

    [Fact]
    public void Only_words_are_kept()
    {
        AzureWordBoundaries.IsWord(SpeechSynthesisBoundaryType.Word).ShouldBeTrue();
        AzureWordBoundaries.IsWord(SpeechSynthesisBoundaryType.Punctuation).ShouldBeFalse();
        AzureWordBoundaries.IsWord(SpeechSynthesisBoundaryType.Sentence).ShouldBeFalse();
    }
}
