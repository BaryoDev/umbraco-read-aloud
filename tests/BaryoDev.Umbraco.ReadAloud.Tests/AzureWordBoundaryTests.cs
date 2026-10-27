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
    public void The_result_matches_what_the_edge_parser_gives_for_the_same_timing()
    {
        // One timing model for both providers: the same word at the same moment must come out
        // identical, or the client would highlight differently depending on the provider.
        var frame = "Path:audio.metadata\r\n\r\n"
            + "{\"Metadata\":[{\"Type\":\"WordBoundary\",\"Data\":{\"Offset\":12500000,"
            + "\"Duration\":3500000,\"text\":{\"Text\":\"Hello\"}}}]}";

        var edge = EdgeTtsFrames.ParseWordBoundaries(frame).Single();
        var azure = AzureWordBoundaries.ToBoundary("Hello", 12_500_000UL, TimeSpan.FromTicks(3_500_000));

        azure.ShouldBe(edge);
    }

    [Fact]
    public void Only_words_are_kept()
    {
        AzureWordBoundaries.IsWord(SpeechSynthesisBoundaryType.Word).ShouldBeTrue();
        AzureWordBoundaries.IsWord(SpeechSynthesisBoundaryType.Punctuation).ShouldBeFalse();
        AzureWordBoundaries.IsWord(SpeechSynthesisBoundaryType.Sentence).ShouldBeFalse();
    }
}
