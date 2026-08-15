## What this changes

<!-- One or two sentences. The diff says what; say why. -->

## Related issue

<!-- Fixes #123, or "none, this is a typo fix". -->

## Checklist

- [ ] A test fails without this change
- [ ] `dotnet test tests/BaryoDev.Umbraco.ReadAloud.Tests` passes locally
- [ ] Still works on Umbraco 16, 17 and 18, or the difference is handled with a conditional
- [ ] `CHANGELOG.md` updated under `## [Unreleased]`

## If it touches the engine or the cache

- [ ] Tests still run without network access. The live test stays `Category=Live` and skipped
- [ ] Any new field that changes the audio is included in the cache key
- [ ] Failures are still not cached

## If it touches what gets stored

- [ ] Still nothing that identifies a listener. No IP, no user agent, no identity
- [ ] Cache keys are still validated as 64 hex characters before becoming a path

## If it touches the browser side

- [ ] No build step added. Plain custom element, no npm, no bundler
- [ ] The fallback to `window.speechSynthesis` still works when the server returns 503
