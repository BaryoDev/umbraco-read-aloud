using System.Text;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class EdgeTtsFrameTests
{
    /// <summary>Builds a binary frame the way the service does: 2-byte big-endian header length, header, audio.</summary>
    private static byte[] BinaryFrame(string header, byte[] audio)
    {
        var headerBytes = Encoding.UTF8.GetBytes(header);
        var frame = new byte[2 + headerBytes.Length + audio.Length];
        frame[0] = (byte)(headerBytes.Length >> 8);
        frame[1] = (byte)(headerBytes.Length & 0xFF);
        headerBytes.CopyTo(frame, 2);
        audio.CopyTo(frame, 2 + headerBytes.Length);
        return frame;
    }

    [Fact]
    public void Audio_is_taken_from_after_the_declared_header_length()
    {
        var audio = new byte[] { 0xFF, 0xFB, 0x90, 0x64 };
        var frame = BinaryFrame("Path:audio\r\nContent-Type:audio/mpeg\r\n\r\n", audio);

        EdgeTtsFrames.AudioPayload(frame).ToArray().ShouldBe(audio);
    }

    [Fact]
    public void A_frame_with_a_header_and_no_audio_yields_nothing()
    {
        // Real streams contain these. Treating the header as audio corrupts the MP3 silently.
        var frame = BinaryFrame("Path:audio\r\n\r\n", []);

        EdgeTtsFrames.AudioPayload(frame).Length.ShouldBe(0);
    }

    [Fact]
    public void A_truncated_frame_yields_nothing_rather_than_throwing()
    {
        EdgeTtsFrames.AudioPayload(new byte[] { 0x00 }).Length.ShouldBe(0);
        EdgeTtsFrames.AudioPayload([]).Length.ShouldBe(0);
    }

    [Fact]
    public void Word_boundaries_are_converted_from_ticks_to_milliseconds()
    {
        // The service reports 100-nanosecond ticks. Skipping the divide makes highlighting run
        // ten thousand times too slow, which looks like the feature is simply broken.
        const string frame =
            "X-RequestId:abc\r\nPath:audio.metadata\r\n\r\n" +
            """
            {"Metadata":[{"Type":"WordBoundary","Data":{"Offset":1000000,"Duration":5000000,"text":{"Text":"Hello"}}}]}
            """;

        var boundaries = EdgeTtsFrames.ParseWordBoundaries(frame);

        boundaries.Count.ShouldBe(1);
        boundaries[0].Text.ShouldBe("Hello");
        boundaries[0].OffsetMs.ShouldBe(100);
        boundaries[0].DurationMs.ShouldBe(500);
    }

    [Fact]
    public void Non_word_metadata_is_ignored()
    {
        const string frame =
            "Path:audio.metadata\r\n\r\n" +
            """
            {"Metadata":[{"Type":"SentenceBoundary","Data":{"Offset":0,"Duration":10,"text":{"Text":"x"}}}]}
            """;

        EdgeTtsFrames.ParseWordBoundaries(frame).ShouldBeEmpty();
    }

    [Fact]
    public void Malformed_metadata_is_ignored_rather_than_failing_the_synthesis()
    {
        // Losing highlighting is a degradation. Losing the audio is a failure.
        EdgeTtsFrames.ParseWordBoundaries("Path:audio.metadata\r\n\r\n{not json").ShouldBeEmpty();
    }

    [Fact]
    public void Well_formed_json_with_a_wrong_typed_offset_is_ignored()
    {
        // Parses fine, so it never reaches the JsonException path. GetDouble on a string throws
        // InvalidOperationException, which is the failure this parser is supposed to absorb.
        const string frame =
            "Path:audio.metadata\r\n\r\n" +
            """
            {"Metadata":[{"Type":"WordBoundary","Data":{"Offset":"not-a-number","Duration":5000000,"text":{"Text":"Hello"}}}]}
            """;

        EdgeTtsFrames.ParseWordBoundaries(frame).ShouldBeEmpty();
    }

    [Fact]
    public void Metadata_that_is_not_an_array_is_ignored()
    {
        const string frame = "Path:audio.metadata\r\n\r\n" + """{"Metadata":"foo"}""";

        EdgeTtsFrames.ParseWordBoundaries(frame).ShouldBeEmpty();
    }

    [Fact]
    public void Turn_end_is_recognised()
    {
        EdgeTtsFrames.IsTurnEnd("X-RequestId:abc\r\nPath:turn.end\r\n\r\n{}").ShouldBeTrue();
        EdgeTtsFrames.IsTurnEnd("Path:turn.start\r\n\r\n{}").ShouldBeFalse();
    }
}
