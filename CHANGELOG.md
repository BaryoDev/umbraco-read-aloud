# Changelog

Notable changes to `BaryoDev.Umbraco.ReadAloud`. Follows [Keep a Changelog](https://keepachangelog.com)
and [semantic versioning](https://semver.org).

## [0.2.0] - Unreleased

### Fixed

- Characters XML 1.0 cannot carry are dropped from SSML before escaping, so one stray control
  character no longer fails a whole synthesis.
- Azure: the idle timeout now applies to the synthesis call itself, the timeout source is disposed
  after the synthesizer, and the region and key are trimmed.

### Added

- A Read Aloud dashboard in the Settings section to set the Azure Speech key from the backoffice,
  with Test, Save and Remove, and no restart. The key is write-only, stored encrypted in the
  Umbraco database with Data Protection and a key ring of its own
  (`umbraco/Data/DataProtection-Keys/ReadAloud`, or `BaryoDev:ReadAloud:KeysPath`). A key from
  configuration still wins and turns off Save and Remove. If the saved key cannot be read, readers
  get the browser voice and the dashboard asks for it again.
- With the Azure package, the voice is chosen per request, so saving or removing a key changes it
  with no restart. `Provider: "Azure"` with no configuration key now boots when a key is saved.
- `IReadAloudEngine.SynthesizesOnServer` (default `true`), which the audio route uses to decide
  between the server voice and a 503 for the browser voice.
- `BaryoDev.Umbraco.ReadAloud.Azure`, a separate package that adds Azure AI Speech as a provider
  through the official Speech SDK, with real word timings. Set `Provider` to `"Azure"` and
  `BaryoDev:ReadAloud:Azure:Region` and `:Key`. The key is read from configuration only.
- `IReadAloudProviderSettings`, which a provider package registers so the core can validate its
  settings at startup.
- `SynthesisRequest.CacheKey(string provider)`. The audio source now caches under the configured
  provider, so switching providers does not serve the other one's audio or timings.

### Changed

- Breaking for Edge users: the default `Provider` is now `"Browser"`, so a site on 0.1.x that
  relied on the Edge default must set `"Provider": "Edge"` (deprecated) or move to `"Azure"`.
  Cache files written by 0.1.x are orphaned because the key now includes the provider; the cache
  folder (`App_Data/BaryoDev/ReadAloud` by default) can be cleared.
- A site's own `IReadAloudEngine` registration still runs under the default `Provider`. Only the
  built-in Browser engine short-circuits to 503.
- The cache key uses the provider of the engine in use (`IReadAloudEngine.Provider`, null for a
  custom engine, which falls back to the configured value). The engine is chosen once at startup,
  so changing `Provider` needs a restart.
- The default `Provider` is now `"Browser"`. With no Azure key, the reader's browser speaks with its
  built-in voice and the server synthesizes nothing. The audio route answers 503 at once, without
  a content lookup or an outbound call, and the client falls back to `speechSynthesis`.
- `Provider: "Edge"` is deprecated and will be removed in a later release. Microsoft now requires a
  token on that endpoint that this package does not generate, so requests are rejected (issue 18).
  It still works as a setting and logs a warning at startup. Use `"Azure"` instead.
- `BrowserVoiceEngine` is the engine registered for the Browser provider. It throws if called.
- `Provider` accepts `"Browser"`, `"Azure"` (when the Azure package is installed) and `"Edge"`. Any
  other name, or Azure with no package, region or key, stops the site at startup and names the setting.
- Version is 0.2.0 for both packages.
- `ReadAloudOptions.SectionName` and `ReadAloudOptions.EdgeProvider` are `static readonly` instead
  of `const`, so their values are read at run time rather than copied into consuming assemblies at
  compile time. A const cannot be changed once anyone has built against it. Source compatible.
  `ReadAloudRateLimiting.PolicyName` stays `const` because it is an attribute argument on the
  controller and attribute arguments must be compile-time constants.

## [0.1.0] - 2026-08-16

First published version, for Umbraco 16, 17 and 18 on .NET 9 and .NET 10. Built against
[the plan](https://github.com/BaryoDev/umbraco-read-aloud/blob/main/docs/superpowers/plans/2026-08-15-umbraco-read-aloud.md).

### Added

- A `<read-aloud>` element that plays a configured property of the current page, with word-by-word
  highlighting driven by the engine's own timings.
- `GET /read-aloud/{nodeKey}` for the audio and `GET /read-aloud/{nodeKey}/timings` for the
  timings, both anonymous and both refusing unpublished nodes, nodes under public access
  protection, and document types the site did not list.
- Synthesis through the free endpoint Microsoft Edge uses, cached on disk under a key derived from
  the text and voice, with one synthesis per key no matter how many readers ask at once.
- Fallback to the browser's `speechSynthesis` when the route is unavailable.
- Configuration under `BaryoDev:ReadAloud`, validated at startup, including a per-IP rate limit and
  a site-wide ceiling on concurrent synthesis.
- Repository furniture: licence, contributing guide, security policy, issue and pull request
  templates.
