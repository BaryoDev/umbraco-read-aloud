using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class EdgeTtsEngineTests
{
    private static EdgeTtsEngine Engine() => new(NullLogger<EdgeTtsEngine>.Instance);

    [Fact]
    public async Task Empty_text_is_rejected_before_a_socket_is_opened()
    {
        // Cheap guard. Opening a connection to send nothing is rude and the server just hangs.
        await Should.ThrowAsync<ArgumentException>(async () =>
            await Engine().SynthesizeAsync(new SynthesisRequest { Text = "   " }));
    }

    [Fact]
    public async Task A_cancelled_request_does_not_hang()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Engine().SynthesizeAsync(new SynthesisRequest { Text = "Hello." }, cts.Token));
    }

    [Fact(Timeout = 5000)]
    public async Task An_idle_connection_times_out_rather_than_hanging()
    {
        using var server = new FakeEdgeServer(async (socket, ct) =>
        {
            // Accepts the connection and then says nothing: the documented behaviour of a
            // rejected Sec-MS-GEC token.
            await Task.Delay(Timeout.Infinite, ct);
        });

        var engine = new EdgeTtsEngine(NullLogger<EdgeTtsEngine>.Instance, server.Url, TimeSpan.FromSeconds(1));

        // Caught rather than asserted through Should.ThrowAsync so a failure names what actually
        // came back. The Shouldly form reports only "should throw X but threw Y", which was enough
        // to know this broke on Linux in Release and not enough to say why: a WebSocketException
        // carries a WebSocketErrorCode and usually an inner exception, and both were invisible.
        var thrown = await Record.ExceptionAsync(() =>
            engine.SynthesizeAsync(new SynthesisRequest { Text = "Hello." }));

        thrown.ShouldNotBeNull("The engine returned rather than timing out.");
        thrown.ShouldBeOfType<TimeoutException>(Describe(thrown));
    }

    /// <summary>
    /// Everything about an exception that matters when one arrives from a socket on a machine you
    /// cannot attach a debugger to.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var lines = new List<string> { $"Got {ex.GetType().FullName}: {ex.Message}" };

        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is WebSocketException ws)
            {
                lines.Add($"  WebSocketErrorCode={ws.WebSocketErrorCode} NativeErrorCode={ws.NativeErrorCode}");
            }

            if (!ReferenceEquals(inner, ex))
            {
                lines.Add($"  inner {inner.GetType().FullName}: {inner.Message}");
            }
        }

        lines.Add(ex.StackTrace ?? "  (no stack trace)");
        return string.Join('\n', lines);
    }

    [Fact(Timeout = 5000)]
    public async Task A_full_exchange_returns_audio_and_word_boundaries()
    {
        string? capturedConfig = null;

        using var server = new FakeEdgeServer(async (socket, ct) =>
        {
            capturedConfig = await ReceiveTextAsync(socket, ct);
            await ReceiveTextAsync(socket, ct); // the ssml message, not asserted here

            var boundaryFrame =
                "X-RequestId:abc\r\nPath:audio.metadata\r\n\r\n"
                + """{"Metadata":[{"Type":"WordBoundary","Data":{"Offset":1000000,"Duration":5000000,"text":{"Text":"Hello"}}}]}""";
            await socket.SendAsync(Encoding.UTF8.GetBytes(boundaryFrame), WebSocketMessageType.Text, true, ct);

            var audio = new byte[] { 0xFF, 0xFB, 0x90, 0x64 };
            var binaryFrame = BinaryFrame("Path:audio\r\nContent-Type:audio/mpeg\r\n\r\n", audio);
            await socket.SendAsync(binaryFrame, WebSocketMessageType.Binary, true, ct);

            var turnEndFrame = Encoding.UTF8.GetBytes("X-RequestId:abc\r\nPath:turn.end\r\n\r\n{}");
            await socket.SendAsync(turnEndFrame, WebSocketMessageType.Text, true, ct);
        });

        var engine = new EdgeTtsEngine(NullLogger<EdgeTtsEngine>.Instance, server.Url, TimeSpan.FromSeconds(5));

        var result = await engine.SynthesizeAsync(new SynthesisRequest { Text = "Hello there." });

        await server.Completion;

        result.ContentType.ShouldBe("audio/mpeg");
        result.Audio.ShouldBe(new byte[] { 0xFF, 0xFB, 0x90, 0x64 });
        result.Boundaries.Count.ShouldBe(1);
        result.Boundaries[0].Text.ShouldBe("Hello");
        result.Boundaries[0].OffsetMs.ShouldBe(100);
        result.Boundaries[0].DurationMs.ShouldBe(500);

        // Covers the concatenated speech.config build: a single wrong brace closes the object a
        // brace short and the service accepts the connection but never replies.
        capturedConfig.ShouldNotBeNull();
        var separator = capturedConfig!.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Should.NotThrow(() => JsonDocument.Parse(capturedConfig[(separator + 4)..]));
    }

    [Fact(Timeout = 5000)]
    public async Task A_voice_containing_ssml_metacharacters_cannot_alter_the_document()
    {
        // Text has always been escaped. Voice, and the three prosody values, were interpolated raw
        // because they only ever came from server configuration. Once a controller lets a visitor
        // choose the voice, an unescaped one closes the voice element early and appends elements of
        // the caller's choosing, sent to Microsoft over the site's own connection.
        // All four, not just the voice. Each is an attribute value in the same document, and
        // reverting the escaping on any one of them alone has to fail this test.
        const string injectedVoice = "x'/><audio src='https://attacker.example/voice'/><voice name='y";
        const string injectedRate = "+0%'/><audio src='https://attacker.example/rate'/><prosody rate='+0%";
        const string injectedPitch = "+0Hz'/><audio src='https://attacker.example/pitch'/><prosody pitch='+0Hz";
        const string injectedVolume = "+0%'/><audio src='https://attacker.example/volume'/><prosody volume='+0%";

        var ssml = await CapturedSsmlAsync(new SynthesisRequest
        {
            Text = "Hello there.",
            Voice = injectedVoice,
            Rate = injectedRate,
            Pitch = injectedPitch,
            Volume = injectedVolume,
        });

        var document = XDocument.Parse(ssml);

        document.Descendants().Count(e => e.Name.LocalName == "audio").ShouldBe(0,
            "an element the caller supplied reached the document Microsoft is asked to speak");
        document.Descendants().Count(e => e.Name.LocalName == "voice").ShouldBe(1);
        document.Descendants().Count(e => e.Name.LocalName == "prosody").ShouldBe(1);

        // Each round-trips as one attribute value, so escaping is what happened rather than
        // stripping, and each is checked separately so one unescaped value cannot hide behind
        // three escaped ones.
        var voice = document.Descendants().Single(e => e.Name.LocalName == "voice");
        voice.Attribute("name")!.Value.ShouldBe(injectedVoice);

        var prosody = document.Descendants().Single(e => e.Name.LocalName == "prosody");
        prosody.Attribute("rate")!.Value.ShouldBe(injectedRate);
        prosody.Attribute("pitch")!.Value.ShouldBe(injectedPitch);
        prosody.Attribute("volume")!.Value.ShouldBe(injectedVolume);
    }

    /// <summary>Runs one full exchange and returns the SSML message the engine sent.</summary>
    private static async Task<string> CapturedSsmlAsync(SynthesisRequest request)
    {
        string? captured = null;

        using var server = new FakeEdgeServer(async (socket, ct) =>
        {
            await ReceiveTextAsync(socket, ct); // speech.config
            captured = await ReceiveTextAsync(socket, ct);

            var audio = BinaryFrame("Path:audio\r\nContent-Type:audio/mpeg\r\n\r\n", [0xFF, 0xFB]);
            await socket.SendAsync(audio, WebSocketMessageType.Binary, true, ct);

            var turnEnd = Encoding.UTF8.GetBytes("X-RequestId:abc\r\nPath:turn.end\r\n\r\n{}");
            await socket.SendAsync(turnEnd, WebSocketMessageType.Text, true, ct);
        });

        var engine = new EdgeTtsEngine(
            NullLogger<EdgeTtsEngine>.Instance, server.Url, TimeSpan.FromSeconds(5));

        await engine.SynthesizeAsync(request);
        await server.Completion;

        captured.ShouldNotBeNull();

        // The frame is headers, a blank line, then the document.
        var separator = captured!.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return captured[(separator + 4)..];
    }

    [Fact(Skip = "Hits the live Microsoft endpoint. Run manually, never in CI.")]
    [Trait("Category", "Live")]
    public async Task Live_synthesis_returns_mp3_and_word_timings()
    {
        var result = await Engine().SynthesizeAsync(new SynthesisRequest
        {
            Text = "There is a listen button on this article.",
            Voice = "en-US-JennyNeural",
        });

        result.ContentType.ShouldBe("audio/mpeg");
        result.Audio.Length.ShouldBeGreaterThan(1000);
        result.Boundaries.ShouldNotBeEmpty();
        result.Boundaries[0].DurationMs.ShouldBeGreaterThan(0);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult received;

        do
        {
            received = await socket.ReceiveAsync(buffer, ct);
            message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);

        return Encoding.UTF8.GetString(message.ToArray());
    }

    /// <summary>Builds a binary frame the way the service does: 2-byte big-endian header length, header, audio.</summary>
    /// <remarks>
    /// A private copy of the helper in EdgeTtsFrameTests.cs, kept separate on purpose so this file
    /// does not depend on that one.
    /// </remarks>
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

    /// <summary>
    /// A minimal WebSocket double standing in for the real Edge endpoint. The engine only ever
    /// runs against a real socket, so the wire behaviour cannot be exercised without one.
    /// </summary>
    /// <summary>
    /// A WebSocket server on loopback that never lets go of its port.
    /// </summary>
    /// <remarks>
    /// This used to use <c>HttpListener</c>, which takes a prefix string rather than an
    /// already-bound socket, so the port had to be known before it could be bound. The only way to
    /// learn a free one was to bind port 0, read what the OS gave back, and release it again. That
    /// gap is a real race, and it bit: with the whole suite running in Release, this fake and
    /// another listener ended up on the same port, and the engine connected to somebody else's
    /// server and read a frame from a different conversation. It surfaced as
    /// <c>WebSocketException: The WebSocket received compressed frame when compression is not
    /// enabled</c>, which points nowhere near the actual cause. See
    /// https://github.com/BaryoDev/umbraco-read-aloud/issues/13
    ///
    /// A <c>TcpListener</c> on port 0 stays bound from the moment the OS assigns the port, so
    /// there is no window at all. The cost is doing the upgrade handshake here, which is about
    /// twenty lines and fully specified: read headers, echo the key hashed with the protocol GUID,
    /// answer 101, then hand the stream to <see cref="WebSocket.CreateFromStream"/>.
    /// </remarks>
    private sealed class FakeEdgeServer : IDisposable
    {
        // RFC 6455 section 1.3. The server proves it understood the handshake by hashing the
        // client's key with this and returning the result.
        private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _handling;

        public int Port { get; }

        public string Url => $"ws://127.0.0.1:{Port}/";

        /// <summary>Completes once the handler has run, or faults with whatever it threw.</summary>
        public Task Completion => _handling;

        public FakeEdgeServer(Func<WebSocket, CancellationToken, Task> handleAsync)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _handling = AcceptAsync(handleAsync);
        }

        private async Task AcceptAsync(Func<WebSocket, CancellationToken, Task> handleAsync)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                await using var stream = client.GetStream();

                var key = await ReadHandshakeKeyAsync(stream, _cts.Token);
                await RespondAsync(stream, key, _cts.Token);

                using var socket = WebSocket.CreateFromStream(
                    stream,
                    new WebSocketCreationOptions { IsServer = true });

                await handleAsync(socket, _cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // Torn down by Dispose before the handler finished. Expected for the timeout test.
            }
        }

        /// <summary>Reads the request headers and returns the client's Sec-WebSocket-Key.</summary>
        private static async Task<string> ReadHandshakeKeyAsync(NetworkStream stream, CancellationToken ct)
        {
            var request = new StringBuilder();
            var one = new byte[1];

            // Byte at a time so the read stops exactly at the end of the headers. Reading into a
            // larger buffer risks swallowing the first WebSocket frame, which the caller has
            // already handed to the WebSocket layer by then.
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, ct) == 0)
                {
                    throw new IOException("The client closed the connection during the handshake.");
                }

                request.Append((char)one[0]);
            }

            var header = request.ToString()
                .Split("\r\n")
                .FirstOrDefault(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("The client sent no Sec-WebSocket-Key.");

            return header["Sec-WebSocket-Key:".Length..].Trim();
        }

        private static async Task RespondAsync(NetworkStream stream, string key, CancellationToken ct)
        {
            var accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + $"Sec-WebSocket-Accept: {accept}\r\n\r\n");

            await stream.WriteAsync(response, ct);
            await stream.FlushAsync(ct);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _ = _handling.ContinueWith(
                t => t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }

            _cts.Dispose();
        }
    }
}
