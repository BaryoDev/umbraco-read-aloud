# Read Aloud for Umbraco

Add "listen to this article" to an Umbraco site, using Microsoft Edge's neural voices. The audio
is generated and cached on your own server, and nothing about a listener is ever stored.

For Umbraco 16, 17 and 18, on .NET 9 and .NET 10.

```
dotnet add package BaryoDev.Umbraco.ReadAloud
```

Nothing else to run: the package registers itself through an Umbraco composer, and every setting
has a working default. The
[design](https://github.com/BaryoDev/umbraco-read-aloud/blob/main/docs/superpowers/specs/2026-08-15-umbraco-read-aloud-design.md)
and the
[plan](https://github.com/BaryoDev/umbraco-read-aloud/blob/main/docs/superpowers/plans/2026-08-15-umbraco-read-aloud.md)
it was built from are in the repository.

---

## Read this first: the endpoint is not supported by Microsoft

This uses the same free endpoint Microsoft Edge uses for its own read-aloud feature. **It is not a
supported Microsoft API.** There is no contract, no SLA and no commitment that it will look the
same next year. Microsoft could change or close it without notice and would owe nobody an
explanation.

**It is unsupported, but it is neither obscure nor new.** Measured 15 August 2026:

| | |
|---|---|
| `edge-tts` on PyPI | 14,645,228 downloads/month |
| `node-edge-tts` on npm | 8,603,847 downloads/month |
| `rany2/edge-tts` on GitHub | 11,727 stars |
| First released | May 2021, still actively maintained |

Roughly 23 million downloads a month, and five years of this path being used at scale without
Microsoft closing it. That is why building on it is reasonable. The absence of a contract is why
there is a fallback rather than none.

**There is no paid provider in this version.** Azure Speech is not implemented: there is no Azure
engine in this package, and no credentials are read anywhere. If you need a supported service with
a contract behind it, register your own `IReadAloudEngine` and the package will use it.

**If the endpoint fails at runtime**, the browser client falls back to `window.speechSynthesis`,
which is built into every modern browser and costs nothing. Quality is worse and word highlighting
is less precise, and the client says so rather than pretending otherwise.

Two tiers: free and good, and free and always available underneath it.

## What it does

- A `<read-aloud>` element that reads a configured property of the current page
- Word-by-word highlighting driven by real timings from the engine, not estimates
- Audio cached on disk, so the same article is never synthesized twice
- Lazy generation, so publishing is instant and nothing is synthesized until somebody asks

## Usage

Load the client on any page you want a button on, then add the element:

```html
<script type="module" src="/App_Plugins/BaryoDev.ReadAloud/readaloud.js"></script>

<read-aloud node="@Model.Key" for="#article-body" voice="en-GB-SoniaNeural"></read-aloud>
```

- `node`: the published content key of the page to read. Required
- `for`: a selector to the element whose words get highlighted as they are spoken, and whose text
  is read by the `speechSynthesis` fallback if the server route fails. Optional; without it the
  button still plays, just without highlighting
- `voice`: optional, honoured only if the site's configuration allows it
- `base`: optional path prefix, only needed if the site is not mounted at the root (an IIS virtual
  application, `UsePathBase`, and similar)

No build step: the file above is a plain ES module, committed as-is in the package.

## What it does not do

- **Store anything about a listener.** No table, no migration, no IP, no user agent, no identity.
  The only thing written anywhere is derived audio you can delete at any time
- **Call any third party except the speech service itself.** No analytics, no telemetry
- **Require a build step.** No npm, no bundler

## Configuration

v1 is configuration only. Every value has a working default.

```json
"BaryoDev": {
  "ReadAloud": {
    "Enabled": true,
    "DocumentTypes": [ "article", "blogPost" ],
    "PropertyAlias": "bodyText",
    "DefaultVoice": "en-GB-SoniaNeural",
    "AllowedVoices": [ "en-GB-SoniaNeural", "en-US-JennyNeural", "fil-PH-BlessicaNeural" ],
    "MaxChars": 8000,
    "CachePath": "App_Data/BaryoDev/ReadAloud",
    "RateLimitPerMinute": 20,
    "Provider": "Edge"
  }
}
```

### Provider

`"Edge"` is the only value this version implements, and it is the default, so there is nothing to
set. Any other value stops the site at startup with a message naming the setting. That is
deliberate: a provider name that looked configured and quietly left every request on the
unofficial endpoint would be worse than a failed boot. To synthesize somewhere else, register your
own `IReadAloudEngine` rather than naming it here.

### RateLimitPerMinute and what "per IP" means

The limit is a fixed one-minute window per source IP **as the server sees it**, which is the
address of whatever connected to it. On a site behind Cloudflare, nginx, a load balancer or any
other reverse proxy, that is the edge's address and not the reader's, so every visitor on the site
shares a single bucket and ordinary readers start getting `429 Too Many Requests`.

If your site sits behind a proxy, configure ASP.NET's forwarded headers middleware
(`UseForwardedHeaders`, with `ForwardedHeaders.XForwardedFor`) and restrict it to your proxy's
addresses. Without it this setting does not do what its name suggests, and raising the number only
delays the problem.

The address is used as an in-memory partition key for the length of one window. It is never
written to disk, never logged, and never part of a cache key.

## Related

The browser half comes from [`@baryodev/read-aloud`](https://www.npmjs.com/package/@baryodev/read-aloud),
which is published and framework-agnostic. This package is the Umbraco-side sibling: it reads the
property server-side, which is the thing a framework-agnostic package cannot do.

## Contributing

Genuinely welcome, including small changes. See
[CONTRIBUTING.md](https://github.com/BaryoDev/umbraco-read-aloud/blob/main/CONTRIBUTING.md), and
look for
[`good first issue`](https://github.com/BaryoDev/umbraco-read-aloud/labels/good%20first%20issue).

## Licence

[MIT](https://github.com/BaryoDev/umbraco-read-aloud/blob/main/LICENSE)
