# Contributing

Contributions are welcome, including small ones. This adds to the
[BaryoDev-wide guide](https://github.com/BaryoDev/.github/blob/main/CONTRIBUTING.md); where the two
disagree, this file wins.

**This package is being built now**, against
[a written plan](docs/superpowers/plans/2026-08-15-umbraco-read-aloud.md). If you want to take
something, comment on its issue first so we do not both write it.

## Getting it running

```sh
git clone https://github.com/BaryoDev/umbraco-read-aloud.git
cd umbraco-read-aloud
dotnet test tests/BaryoDev.Umbraco.ReadAloud.Tests
```

That boots a real Umbraco on SQLite. The first run takes a while because Umbraco cold-boots; after
that it is seconds.

**Tests never call Microsoft.** The engine is tested against recorded frames, so the suite works
offline and does not depend on a service nobody controls. One test hits the live endpoint and is
marked `[Trait("Category", "Live")]`, skipped by default and excluded from CI. Run it deliberately
when you change the protocol.

## Four constraints that decide most design questions

These are the reasons to choose this over a hosted service, so a change breaking one is usually
the wrong change even when it is convenient.

**Nothing about a listener is ever stored.** No table, no migration, no IP, no user agent, no
identity. The only persisted state is derived audio on disk. If a change would let this package
answer "who listened", it needs a different design.

**Nothing leaves the server except the synthesis call.** No analytics, no telemetry, no third
party at runtime.

**No build step for the site owner.** Browser assets ship compiled inside the package. Any UI is a
plain custom element using the `uui-*` components Umbraco already ships. If a change means someone
has to run npm, it is the wrong change.

**The engine is behind an interface.** Everything above `IReadAloudEngine` is unaware of which
implementation is running, so another service could be put behind it without the controller, the
cache or the client changing. This version ships one engine and no second provider is configurable.
Do not let provider details leak upward.

## Things worth knowing before you change something

**The cache key is the whole cache design.** It is a sha256 of voice, rate, pitch, volume and the
text. Because the text is in it, editing a page changes the key, so a stale recording can never be
served and nothing needs to invalidate anything. If you add a field that changes the audio, it
must go in the key.

**Generation is lazy on purpose.** Nothing is synthesized on publish. Eager generation would mean
a full site republish sends thousands of requests to an unofficial endpoint in a burst, which is a
good way to get it closed for everyone. Do not add bulk pre-generation.

**One synthesis per key at a time.** An article shared widely means many readers press Listen
before the first synthesis finishes. Without coalescing that is one WebSocket each.

**Failures are never cached.** Caching one would poison that article permanently, and the next
reader inherits an outage that passed hours ago.

**The `Sec-MS-GEC` token is the thing most likely to bite you.** It is a hash of a Windows file
time floored to a five minute window. Get it wrong and the socket opens, the server accepts it,
and then simply never replies. It looks like a hang, not an error.

**Umbraco 16, 17 and 18 differ more than you would like.** `IPublishedContentCache.GetAtRoot` and
`GetByRoute` were obsoleted in 16 and removed in 17. Before using any Umbraco API, check it exists
in all three by diffing the shipped `Umbraco.Core.xml` for 16.5.1, 17.6.1 and 18.1.0. A change that
only works on one needs a conditional, not a version bump.

## Tests

**Every change needs a test that fails without it.** If you cannot write one, say so in the
description and explain why.

Tests boot a real Umbraco rather than a mock, on purpose. Route registration, DI and options
binding are only exercised by a real boot, and a test double passes with all three broken.

## Licensing

MIT. New files need no header.

## Reporting a security issue

Do not open a public issue. See [SECURITY.md](SECURITY.md).
