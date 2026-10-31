# Read Aloud for Umbraco

Add "listen to this article" to an Umbraco site. By default the reader's browser speaks the text
with its own built-in voice. For natural neural voices with word highlighting, add the Azure
package. Audio from the server is cached on your own server, and nothing about a listener is ever
stored.

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

## Voices

By default the package uses the browser's built-in voice. Nothing is synthesized on your server and
nothing is sent to a third party. Quality depends on the device: good on iPhone, Mac and recent
Android, robotic on many Windows and Linux machines. Word highlighting is less precise than with a
server voice.

For natural neural voices with word highlighting, install `BaryoDev.Umbraco.ReadAloud.Azure`, then paste
the Speech key into the Read Aloud dashboard in the Settings section. No server access and no
restart. Configuration can set the key instead. See [Azure AI Speech](#azure-ai-speech). The Azure
free tier is 500,000 characters a month.

**Edge is deprecated.** `Provider: "Edge"` used the free endpoint Microsoft Edge uses for its own
read-aloud feature. It was never a supported API, and Microsoft now requires a token on it that
this package does not generate, so requests are rejected (see
[issue 18](https://github.com/BaryoDev/umbraco-read-aloud/issues/18)). It is still selectable, logs a
warning at startup, and will be removed in a later release. Use Azure instead.

If a server voice fails at runtime, the route answers 503 and the browser falls back to its own
voice.

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
- `base`: optional path prefix for a site not mounted at the root (an IIS virtual application,
  `UsePathBase`, and similar). The client works the prefix out from the URL this script was served
  from, so it is only needed if you serve the file from somewhere other than `App_Plugins`

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
    "MaxConcurrentSynthesis": 4,
    "Provider": "Browser"
  }
}
```

### Provider

`"Browser"` is the default, so there is nothing to set. `"Azure"` needs the
[Azure package](#azure-ai-speech). `"Edge"` is deprecated (see [Voices](#voices)). A value with no
provider behind it, or `"Azure"` with the package missing, or with no key in configuration and none saved in the backoffice, stops the site
at startup with a message naming the setting. To synthesize somewhere else, register your own
`IReadAloudEngine` (after the package's composers, so yours is the one resolved). A registered
engine runs whatever `Provider` says, and caches under the configured `Provider` unless it
overrides `IReadAloudEngine.Provider`.

With the Azure package installed, the voice is chosen on every request, so a key saved or removed in
the backoffice applies to the next request. Without it, the engine is chosen once, when the site
starts, and changing `Provider` needs a restart.

With `"Browser"` the audio route answers 503 straight away, with no content lookup and no outbound
call, and the client script speaks with `speechSynthesis`. The client makes one small request per
page and then stays on the browser voice.

The provider is part of the cache key, so switching it does not serve audio or timings made by the
other one. The first request for each article after a switch synthesizes again. Cache files made by 0.1.x are
never read again (the key now includes the provider), so the cache folder can be cleared.

## Azure AI Speech

Microsoft's supported speech service, with the same neural voices (for example `en-PH-RosaNeural`).
Use it when you want server-made audio with a contract behind it.

```
dotnet add package BaryoDev.Umbraco.ReadAloud.Azure
```

Create a Speech resource in the Azure portal. Then set the key in either of two ways.

### In the backoffice (the normal way)

Open Settings, then the Read Aloud tab. Enter the region of the resource (for example
`southeastasia`) and the key, press Test, then Save. Readers get the Azure voice from the next
request. Remove puts the browser voice back, also with no restart. Only users with access to the
Settings section can open it, and the API behind it checks the same permission.

- Test makes a very short real synthesis with what you typed, or with the saved key if the key box
  is empty. It saves nothing. A failure says the key was rejected, the region looks wrong, or the
  server could not reach Azure. Azure answers a wrong key and a wrong region the same way, so the
  message names both.
- The key is write-only. The dashboard and the API only say whether a key is saved and which region
  it is for. Nothing returns the key or part of it, and it is never logged.
- Leave the key box empty on Save to keep the saved key and change only the region.

The key is stored encrypted in the Umbraco database (the `umbracoKeyValue` table) with ASP.NET Core
Data Protection. It uses a key ring of its own, kept in `umbraco/Data/DataProtection-Keys/ReadAloud`
under the site root (set `BaryoDev:ReadAloud:KeysPath` to move it). **Keep that folder across
deploys.** If it is lost or the database is copied to a site without it, the saved key cannot be
read: readers get the browser voice, the dashboard says "Saved key can no longer be read, enter it
again", and entering it again fixes it. The ring is plain key files on disk, so someone who can read
both the database and that folder can decrypt the key.

With several servers behind a load balancer, point `KeysPath` at a folder they share. A change made
on one server reaches the others within about 15 seconds.

### In configuration (an override)

If configuration supplies a key, it wins. The dashboard then shows that the key is set by
configuration and turns off Save and Remove.

```json
"BaryoDev": {
  "ReadAloud": {
    "Provider": "Azure",
    "DefaultVoice": "en-PH-RosaNeural",
    "AllowedVoices": [ "en-PH-RosaNeural" ],
    "Azure": {
      "Region": "southeastasia"
    }
  }
}
```

**The key goes in an environment variable or user secrets, never in an `appsettings` file that is
committed to git.**

```
BaryoDev__ReadAloud__Azure__Key=<your key>
```

```
dotnet user-secrets set "BaryoDev:ReadAloud:Azure:Key" "<your key>"
```

Order of precedence: a key from configuration (with `Provider` set to `"Azure"`), then the key saved
in the backoffice, then the browser voice. A site with `Provider` left at `"Browser"` and a saved
key gets Azure. `Provider: "Edge"` stays Edge.

Startup: `Provider: "Azure"` with no key in configuration and no saved key stops the site at
startup and names the setting, not the value. With a saved key it boots. A site with no Azure
settings at all always boots.

Without the Azure package the dashboard says to install it and shows no fields.

The free F0 tier of Azure AI Speech includes 500,000 neural characters a month. Check Azure's
pricing page for the current figure, since it is Microsoft's to change. Audio is cached on your
server, so each article is synthesized once per voice and edit.

**Why a separate package.** It uses the official Speech SDK, because word-by-word highlighting
needs real WordBoundary timings and the plain REST endpoint does not return them. The SDK ships
native binaries. Keeping it out of the core means sites on the browser voice do not carry them.

Failures: the route answers 503 and the browser falls back to
`speechSynthesis`. The audio is `audio-24khz-48kbitrate-mono-mp3`, the same format the browser client already plays.

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

### MaxConcurrentSynthesis

A ceiling on how many articles are being synthesized at once across the whole site, which is a
different quantity from the rate limit: the rate limit counts requests arriving, and one synthesis
of a long article outlives the request that asked for it, deliberately, so the cache is filled for
whoever asks next. Past the ceiling a request is refused with a 503 and the reader gets the
browser's own voice, rather than waiting on a queue with no end. Four is deliberately modest,
because a burst of connections is what gets an unofficial endpoint closed.

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
