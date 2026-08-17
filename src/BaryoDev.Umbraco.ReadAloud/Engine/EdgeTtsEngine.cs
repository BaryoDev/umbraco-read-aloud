using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.ReadAloud.Engine;

/// <summary>
/// Speaks the Edge read-aloud WebSocket protocol.
/// </summary>
/// <remarks>
/// This is server-side by necessity, not by preference. The endpoint requires an Origin naming a
/// specific Edge extension and a matching User-Agent, and browsers put both on the forbidden
/// header list precisely so a page cannot claim to be something else. A spike confirmed
/// ClientWebSocket accepts both on .NET, which is the only reason this port is possible.
///
/// The endpoint is not a supported Microsoft API. See SECURITY.md and the README.
/// </remarks>
public sealed class EdgeTtsEngine : IReadAloudEngine
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<EdgeTtsEngine> _logger;
    private readonly string _endpoint;
    private readonly TimeSpan _idleTimeout;

    public EdgeTtsEngine(ILogger<EdgeTtsEngine> logger)
        : this(logger, EdgeTtsProtocol.WssUrl, DefaultIdleTimeout)
    {
    }

    internal EdgeTtsEngine(ILogger<EdgeTtsEngine> logger, string endpoint, TimeSpan idleTimeout)
    {
        _logger = logger;
        _endpoint = endpoint;
        _idleTimeout = idleTimeout;
    }

    public async Task<SynthesisResult> SynthesizeAsync(
        SynthesisRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("Text is empty.", nameof(request));
        }

        ct.ThrowIfCancellationRequested();

        const string format = "audio-24khz-48kbitrate-mono-mp3";
        var connectionId = Guid.NewGuid().ToString("N");

        var url =
            $"{_endpoint}?TrustedClientToken={EdgeTtsProtocol.TrustedClientToken}"
            + $"&Sec-MS-GEC={EdgeTtsProtocol.SecMsGecToken(DateTimeOffset.UtcNow)}"
            + $"&Sec-MS-GEC-Version=1-{EdgeTtsProtocol.ChromiumVersion}"
            + $"&ConnectionId={connectionId}";

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", EdgeTtsProtocol.ExtensionOrigin);
        socket.Options.SetRequestHeader("User-Agent", EdgeTtsProtocol.UserAgent());

        // Idle, not total-duration: the clock is reset on every frame received below, so a long
        // article does not get punished, only silence does.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_idleTimeout);

        try
        {
            await socket.ConnectAsync(new Uri(url), idle.Token);

            var timestamp = DateTimeOffset.UtcNow.ToString("O");

            // Built by concatenation rather than interpolation. In an interpolated string every brace
            // in this JSON would need doubling, and getting one wrong closes the object a brace short.
            // The server then accepts the connection and simply never replies.
            var config =
                "X-Timestamp:" + timestamp + "\r\n"
                + "Content-Type:application/json; charset=utf-8\r\n"
                + "Path:speech.config\r\n\r\n"
                + "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{"
                + "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\""
                + (request.WordBoundaries ? "true" : "false")
                + "\"},\"outputFormat\":\"" + format + "\"}}}}";

            await SendAsync(socket, config, idle.Token);

            // Every value is escaped, not only the text. These four are attribute values in a
            // document sent to a third party, and this class is public API: it cannot assume its
            // caller vetted them, and the controller that serves visitors does not write them by
            // hand. An unescaped quote closes the attribute and appends elements of the caller's
            // choosing to what Microsoft is asked to speak.
            var ssml =
                "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>"
                + $"<voice name='{EdgeTtsProtocol.EscapeXml(request.Voice)}'>"
                + $"<prosody pitch='{EdgeTtsProtocol.EscapeXml(request.Pitch)}'"
                + $" rate='{EdgeTtsProtocol.EscapeXml(request.Rate)}'"
                + $" volume='{EdgeTtsProtocol.EscapeXml(request.Volume)}'>"
                + EdgeTtsProtocol.EscapeXml(request.Text)
                + "</prosody></voice></speak>";

            await SendAsync(socket,
                $"X-RequestId:{connectionId}\r\nX-Timestamp:{timestamp}\r\n"
                + "Content-Type:application/ssml+xml\r\nPath:ssml\r\n\r\n" + ssml, idle.Token);

            var audio = new MemoryStream();
            var boundaries = new List<WordBoundary>();
            var buffer = new byte[16 * 1024];

            while (socket.State == WebSocketState.Open)
            {
                var frame = new MemoryStream();
                WebSocketReceiveResult received;

                do
                {
                    received = await socket.ReceiveAsync(buffer, idle.Token);
                    idle.CancelAfter(_idleTimeout);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    frame.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                if (received.MessageType == WebSocketMessageType.Close) break;

                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    var payload = EdgeTtsFrames.AudioPayload(frame.GetBuffer().AsSpan(0, (int)frame.Length));
                    if (payload.Length > 0) audio.Write(payload);
                    continue;
                }

                var text = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);

                if (request.WordBoundaries && text.Contains("Path:audio.metadata", StringComparison.Ordinal))
                {
                    boundaries.AddRange(EdgeTtsFrames.ParseWordBoundaries(text));
                }

                if (EdgeTtsFrames.IsTurnEnd(text)) break;
            }

            if (audio.Length == 0)
            {
                throw new InvalidOperationException("The service closed the connection before sending any audio.");
            }

            return new SynthesisResult(audio.ToArray(), boundaries, "audio/mpeg");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The speech service sent nothing for {_idleTimeout.TotalSeconds:0} seconds. "
                + "The Sec-MS-GEC token being rejected is the usual cause, since the service accepts the "
                + "connection and then stays silent rather than returning an error.");
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string message, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct);
}
