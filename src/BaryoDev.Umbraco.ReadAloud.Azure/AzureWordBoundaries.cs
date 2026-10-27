using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.CognitiveServices.Speech;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>
/// Turns Speech SDK word boundary events into the timings the core already uses for Edge.
/// </summary>
/// <remarks>
/// Kept apart from the synthesizer so it can be tested without a network. The SDK reports the
/// audio offset in 100-nanosecond ticks, the same unit the Edge frames use, and the core works in
/// milliseconds.
/// </remarks>
internal static class AzureWordBoundaries
{
    /// <summary>
    /// Only words. The SDK also raises events for punctuation and sentences, and the Edge path
    /// sends words alone, so the client sees the same shape from either provider.
    /// </summary>
    internal static bool IsWord(SpeechSynthesisBoundaryType type) =>
        type == SpeechSynthesisBoundaryType.Word;

    internal static WordBoundary ToBoundary(string text, ulong audioOffsetTicks, TimeSpan duration) =>
        new(text, audioOffsetTicks / 10000.0, duration.TotalMilliseconds);
}
