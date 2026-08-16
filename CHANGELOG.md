# Changelog

Notable changes to `BaryoDev.Umbraco.ReadAloud`. Follows [Keep a Changelog](https://keepachangelog.com)
and [semantic versioning](https://semver.org).

## [Unreleased]

Nothing yet.

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
