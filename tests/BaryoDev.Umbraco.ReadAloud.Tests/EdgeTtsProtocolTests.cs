using BaryoDev.Umbraco.ReadAloud.Engine;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class EdgeTtsProtocolTests
{
    [Fact]
    public void The_token_is_stable_within_a_five_minute_window()
    {
        // The TypeScript rounds the clock down to a 300 second window before hashing, so every
        // call inside one window must produce the same token. Without the rounding the token
        // changes every second, the handshake still succeeds, and the server simply never
        // replies, which is a miserable thing to debug.
        var start = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

        EdgeTtsProtocol.SecMsGecToken(start)
            .ShouldBe(EdgeTtsProtocol.SecMsGecToken(start.AddSeconds(299)));
    }

    [Fact]
    public void The_token_changes_in_the_next_window()
    {
        var start = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

        EdgeTtsProtocol.SecMsGecToken(start)
            .ShouldNotBe(EdgeTtsProtocol.SecMsGecToken(start.AddSeconds(300)));
    }

    [Fact]
    public void The_token_is_uppercase_hex_of_a_sha256()
    {
        var token = EdgeTtsProtocol.SecMsGecToken(DateTimeOffset.UtcNow);

        token.Length.ShouldBe(64);
        token.ShouldBe(token.ToUpperInvariant());
        token.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Fact]
    public void Xml_special_characters_are_escaped_so_ssml_cannot_be_broken()
    {
        // The text goes inside an SSML document. Unescaped content silently corrupts the
        // request, and an ampersand in an article title is enough to do it.
        EdgeTtsProtocol.EscapeXml("Tom & Jerry <b>\"quoted\"</b> 'x'")
            .ShouldBe("Tom &amp; Jerry &lt;b&gt;&quot;quoted&quot;&lt;/b&gt; &apos;x&apos;");
    }

    [Fact]
    public void The_user_agent_looks_like_chromium_built_from_the_pinned_version()
    {
        // The server inspects this header. A malformed value produces the same failure mode as
        // a bad token: the socket opens, the server accepts it, and nothing ever arrives. The
        // Chrome and Edg segments are asserted against the constant, not a literal, so the two
        // can never drift apart when the pinned Chromium version is bumped.
        var userAgent = EdgeTtsProtocol.UserAgent();

        userAgent.ShouldStartWith("Mozilla/5.0");
        userAgent.ShouldContain($"Chrome/{EdgeTtsProtocol.ChromiumVersion}");
        userAgent.ShouldContain($"Edg/{EdgeTtsProtocol.ChromiumVersion}");
    }
}
