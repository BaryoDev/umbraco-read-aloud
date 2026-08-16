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
        <p>Every word you are hearing was generated a moment ago by the server that sent you this
        page, and then written to a file cache so that nobody has to wait for it twice. Nothing
        about the fact that you pressed Listen was recorded anywhere.</p>

        <p>The voice belongs to Microsoft Edge. Edge has had a read aloud feature for years, and it
        does not run the voices on your machine. It opens a WebSocket to a Microsoft endpoint, sends
        the text, and gets back an MP3 stream along with a running commentary of which word starts
        at which millisecond. That commentary is what drives the highlight moving through this
        paragraph.</p>

        <p>The endpoint is not a supported Microsoft product. There is no contract behind it, no
        service level agreement, and no promise that it will look the same next year. It is worth
        being blunt about that, because the honest version of this pitch is not that the risk is
        absent, but that it is known and it is hedged.</p>

        <p>It is also neither obscure nor new. The Python library that speaks this protocol is
        downloaded about fifteen million times a month. Its Node counterpart adds another eight
        million. The original project has been maintained since 2021 and has thousands of stars.
        Five years at that volume, without Microsoft closing the door, is a reasonable basis for
        building on it. The absence of a contract is why there are two fallbacks rather than
        none.</p>

        <p>The first fallback is money. Point the configuration at Azure Speech and you get the same
        neural voices with an invoice and a support number attached. The second is your own browser.
        Every modern browser ships speech synthesis built in, and if the server route fails the
        button switches to it and says so rather than pretending the quality is the same.</p>

        <h2>Why the server does this and not the page</h2>

        <p>A browser cannot talk to that endpoint. It requires an Origin header naming a specific
        Edge extension and a matching User Agent, and browsers refuse to let a page set either. That
        is the whole reason this is a server side package rather than a script you drop into a
        template.</p>

        <p>Doing it on the server turns out to be the better place anyway. The server already knows
        which property of which published page holds the article text, so the browser never sends
        any text at all. It sends a page key. That removes a whole class of abuse, because there is
        no way to hand this endpoint an arbitrary block of writing and have it read out. It does not
        accept text.</p>

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
