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
        + $"<voice name='{EdgeTtsProtocol.EscapeXml(request.Voice)}'>"
        + $"<prosody pitch='{EdgeTtsProtocol.EscapeXml(request.Pitch)}'"
        + $" rate='{EdgeTtsProtocol.EscapeXml(request.Rate)}'"
        + $" volume='{EdgeTtsProtocol.EscapeXml(request.Volume)}'>"
        + EdgeTtsProtocol.EscapeXml(request.Text)
        + "</prosody></voice></speak>";
}
