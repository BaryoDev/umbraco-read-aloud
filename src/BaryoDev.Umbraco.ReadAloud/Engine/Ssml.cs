namespace BaryoDev.Umbraco.ReadAloud.Engine;

/// <summary>
/// Builds the SSML document an engine sends to a speech service.
/// </summary>
/// <remarks>
/// Shared by every engine so there is one place that decides what is escaped. Every value is
/// escaped, not only the text. These four are attribute values in a document sent to a third
/// party, and the engines are public API: they cannot assume their caller vetted them, and the
/// controller that serves visitors does not write them by hand. An unescaped quote closes the
/// attribute and appends elements of the caller's choosing to what the service is asked to speak.
/// </remarks>
internal static class Ssml
{
    internal static string Build(SynthesisRequest request) =>
        "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>"
        + $"<voice name='{EscapeXml(request.Voice)}'>"
        + $"<prosody pitch='{EscapeXml(request.Pitch)}'"
        + $" rate='{EscapeXml(request.Rate)}'"
        + $" volume='{EscapeXml(request.Volume)}'>"
        + EscapeXml(request.Text)
        + "</prosody></voice></speak>";

    /// <summary>
    /// Escapes for XML text and attribute values. Characters XML 1.0 cannot carry at all (most
    /// control characters, lone surrogates, U+FFFE and U+FFFF) are dropped first, because escaping
    /// cannot make them legal and the service rejects the whole document.
    /// </summary>
    internal static string EscapeXml(string s) => StripInvalidXmlChars(s)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    internal static string StripInvalidXmlChars(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[++i]);
            }
            else if (c is '\t' or '\n' or '\r' or (>= ' ' and <= '\uD7FF') or (>= '\uE000' and <= '\uFFFD'))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
