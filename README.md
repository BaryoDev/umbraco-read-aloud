# Read Aloud for Umbraco

Add "listen to this article" to an Umbraco site, using Microsoft Edge's neural voices. The audio
is generated and cached on your own server, and nothing about a listener is ever stored.

**Status: in development.** Nothing is published yet. See
[the design](docs/superpowers/specs/2026-08-15-umbraco-read-aloud-design.md) and
[the plan](docs/superpowers/plans/2026-08-15-umbraco-read-aloud.md).

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
there are two fallbacks rather than none.

**If you need a guarantee**, configure the Azure Speech provider: the same neural voices, with a
contract and a bill.

**If the endpoint fails at runtime**, the browser client falls back to `window.speechSynthesis`,
which is built into every modern browser and costs nothing. Quality is worse and word highlighting
is less precise, and the client says so rather than pretending otherwise.

Three tiers: free and good, paid and guaranteed, free and always available.

## What it will do

- A `<read-aloud>` element that reads a configured property of the current page
- Word-by-word highlighting driven by real timings from the engine, not estimates
- Audio cached on disk, so the same article is never synthesized twice
- Lazy generation, so publishing is instant and nothing is synthesized until somebody asks

## What it will not do

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
    "RateLimitPerMinute": 20
  }
}
```

## Related

The browser half comes from [`@baryodev/read-aloud`](https://www.npmjs.com/package/@baryodev/read-aloud),
which is published and framework-agnostic. This package is the Umbraco-side sibling: it reads the
property server-side, which is the thing a framework-agnostic package cannot do.

## Contributing

Genuinely welcome, including small changes. See [CONTRIBUTING.md](CONTRIBUTING.md), and look for
[`good first issue`](https://github.com/BaryoDev/umbraco-read-aloud/labels/good%20first%20issue).

## Licence

[MIT](LICENSE)
