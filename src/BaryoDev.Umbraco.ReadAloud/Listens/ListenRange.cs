using Microsoft.Extensions.Primitives;

namespace BaryoDev.Umbraco.ReadAloud.Listens;

/// <summary>
/// Which audio requests count as a play, judged by the <c>Range</c> header alone.
/// </summary>
/// <remarks>
/// The origin answers every range request with the whole recording and a 200, so a browser that
/// probes with a partial range would otherwise count twice for one press. A request with no range,
/// or the open range from the start, is the press itself. Everything else is a probe or a seek and
/// counts nothing. Kept in one place because a real Safari capture may change it.
/// </remarks>
internal static class ListenRange
{
    private const string Unit = "bytes=";

    /// <summary>True when there is no range, or exactly one range that is <c>bytes=0-</c>.</summary>
    internal static bool CountsAsPlay(StringValues range)
    {
        if (StringValues.IsNullOrEmpty(range)) return true;
        if (range.Count != 1) return false;

        var value = range[0]!.Trim();
        if (!value.StartsWith(Unit, StringComparison.OrdinalIgnoreCase)) return false;

        return value[Unit.Length..].Trim() == "0-";
    }
}
