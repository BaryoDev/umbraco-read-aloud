using System.Security.Cryptography;
using System.Text;

namespace BaryoDev.Umbraco.ReadAloud.Engine;

/// <summary>
/// Constants and pure helpers for the Edge read-aloud WebSocket protocol.
/// </summary>
/// <remarks>
/// Separated from the socket work so the fiddly parts are testable without a network. The token
/// is the piece most likely to be got wrong: it is a hash of a Windows file time rounded down to
/// a five minute window, and an incorrect one produces a socket that opens and then stays silent
/// rather than an error.
/// </remarks>
internal static class EdgeTtsProtocol
{
    internal const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    internal const string ChromiumVersion = "134.0.3124.66";
    internal const string ExtensionOrigin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";
    internal const string WssUrl =
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";

    /// <summary>
    /// The Sec-MS-GEC query value: SHA256 of (Windows file time, floored to 300 seconds) plus the
    /// trusted client token, uppercase hex.
    /// </summary>
    /// <remarks>
    /// The TypeScript computes (unixSeconds + 11644473600) * 10^7 with BigInt. That constant is
    /// the 1601-to-1970 epoch offset, which means the expression is a Windows file time and .NET
    /// has ToFileTimeUtc built in.
    /// </remarks>
    internal static string SecMsGecToken(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        seconds -= seconds % 300;

        var fileTime = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToFileTimeUtc();
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes($"{fileTime}{TrustedClientToken}"));

        return Convert.ToHexString(hash);
    }

    internal static string UserAgent() =>
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + $"Chrome/{ChromiumVersion} Safari/537.36 Edg/{ChromiumVersion}";

    internal static string EscapeXml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
