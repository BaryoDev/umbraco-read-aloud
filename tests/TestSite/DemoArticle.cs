namespace TestSite;

/// <summary>
/// The body of the demo article.
/// </summary>
/// <remarks>
/// Long enough to be worth listening to, and about the thing it is demonstrating, so that a
/// visitor who presses Listen learns what the package does by hearing it rather than by reading
/// a second page. Markup rather than plain text, because the extractor's job is to turn markup
/// into speakable text and a demo made of one bare sentence would never exercise it.
/// </remarks>
public static class DemoArticle
{
    /// <summary>The published value of the article's body property.</summary>
    public const string BodyHtml = """
        <p>By default the words you are hearing are spoken by your own browser, using the voice it
        ships with. The server that sent you this page synthesizes nothing, and nothing about the
        fact that you pressed Listen was recorded anywhere.</p>

        <p>A site can switch on a neural voice by installing the Azure package and pasting in a key
        for its own Azure Speech resource. The server then sends the article text to that service,
        gets back an MP3 stream along with a running commentary of which word starts at which
        millisecond, and writes both to a file cache so that nobody has to wait for them twice. That
        commentary is what drives the highlight moving through this paragraph.</p>

        <p>If the server route fails, the button switches to the browser voice and says so rather
        than pretending the quality is the same. Configuring a provider that does not exist stops
        the site at startup rather than quietly using another one.</p>

        <h2>Why the server does this and not the page</h2>

        <p>The speech key has to stay on the server. A page that held it would hand it to every
        visitor, so the synthesis is a server side job and the browser only ever asks for audio.</p>

        <p>Doing it on the server turns out to be the better place anyway. The server already knows
        which property of which published page holds the article text, so the browser never sends
        any text at all. It sends a page key. That removes a whole class of abuse, because there is
        no way to hand the speech service an arbitrary block of writing and have it read out. It
        does not accept text.</p>

        <h2>What gets stored</h2>

        <p>Audio, and nothing else. The cache is keyed on the text and the voice, so two pages with
        identical wording share one recording, and an edit to this paragraph produces a new one on
        the next request. Delete the cache folder at any time and the only cost is that the next
        listener waits a few seconds.</p>

        <p>There is no listener table. No address, no user agent, no identity, and no count of how
        many times this article has been played. The requesting address is held in memory for up to
        one minute as a rate limiting bucket and then forgotten. That is a decision rather than an
        oversight. A read aloud button is an accessibility feature, and somebody who needs one
        should not have to pay for it with a record of having needed it.</p>

        <h2>Try it cold</h2>

        <p>The first request for a given article and voice is slow, because it is doing real work
        over a socket to a service on the other side of the world. The second is fast, because it is
        a file read. The two commands at the bottom of this page let you watch the difference for
        yourself.</p>
        """;
}
