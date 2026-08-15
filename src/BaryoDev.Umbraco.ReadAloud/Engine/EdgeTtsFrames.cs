using System.Text.Json;

namespace BaryoDev.Umbraco.ReadAloud.Engine;

/// <summary>
/// Parses the frames the Edge read-aloud service sends.
/// </summary>
/// <remarks>
/// Deliberately free of any socket, so the wire format can be tested without depending on
/// Microsoft being reachable. Two shapes arrive: binary frames carrying a header then audio, and
/// text frames carrying either word timings or a turn marker.
/// </remarks>
internal static class EdgeTtsFrames
{
    /// <summary>
    /// The audio in a binary frame, which follows a 2-byte big-endian header length and the
    /// header itself.
    /// </summary>
    internal static ReadOnlySpan<byte> AudioPayload(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2) return default;

        var headerLength = (frame[0] << 8) | frame[1];
        var start = 2 + headerLength;

        return start >= frame.Length ? default : frame[start..];
    }

    /// <summary>Word timings from a Path:audio.metadata frame, in milliseconds.</summary>
    internal static IReadOnlyList<WordBoundary> ParseWordBoundaries(string frame)
    {
        var separator = frame.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0) return [];

        var boundaries = new List<WordBoundary>();

        try
        {
            using var document = JsonDocument.Parse(frame[(separator + 4)..]);

            if (!document.RootElement.TryGetProperty("Metadata", out var metadata)) return [];

            foreach (var entry in metadata.EnumerateArray())
            {
                if (!entry.TryGetProperty("Type", out var type)
                    || type.GetString() != "WordBoundary"
                    || !entry.TryGetProperty("Data", out var data))
                {
                    continue;
                }

                var text = data.TryGetProperty("text", out var textNode)
                           && textNode.TryGetProperty("Text", out var value)
                    ? value.GetString() ?? ""
                    : "";

                // The service reports 100-nanosecond ticks; everything above this works in ms.
                var offset = data.TryGetProperty("Offset", out var o) ? o.GetDouble() / 10000 : 0;
                var duration = data.TryGetProperty("Duration", out var d) ? d.GetDouble() / 10000 : 0;

                boundaries.Add(new WordBoundary(text, offset, duration));
            }
        }
        catch (JsonException)
        {
            // Losing highlighting is a degradation; losing the audio would be a failure.
            return [];
        }

        return boundaries;
    }

    internal static bool IsTurnEnd(string frame) =>
        frame.Contains("Path:turn.end", StringComparison.Ordinal);
}
