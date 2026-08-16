# Security

## Reporting

**Please do not open a public issue.** Use
[GitHub's private vulnerability reporting](https://github.com/BaryoDev/umbraco-read-aloud/security/advisories/new),
or the contact route in the [BaryoDev policy](https://github.com/BaryoDev/.github/blob/main/SECURITY.md).

## What this package touches

**Two anonymous endpoints exist**, both unauthenticated because every visitor's browser calls
them:

- `GET /read-aloud/{nodeKey}` returns the MP3.
- `GET /read-aloud/{nodeKey}/timings` returns the word timings that drive highlighting: a JSON
  array of one entry per spoken word, each carrying that word's text and its offset in the
  recording. **This route discloses the article's text word by word**, in reading order, without
  fetching any audio. Treat it as equivalent to the audio route for any question about who may
  read a page, not as metadata about it.

Both routes run the same guards, from one shared method called as the first statement of each
action:

- **They take no text.** The server reads the configured property itself. There is no
  arbitrary-text endpoint here and therefore no abuse surface for one.
- They are capped by `MaxChars` and rate limited per IP.
- `AllowedVoices` restricts what a caller may request; anything else falls back to the default.
- They serve only **published** nodes, so unpublished content cannot be read out.
- They refuse a node under public access protection, including protection inherited from an
  ancestor, and refuse it with a 404 so that a refusal does not confirm the node exists. Attribute
  routed controllers never run Umbraco's routing pipeline, so this check is made in the controller
  rather than inherited.

A node key is not a secret: it is in the page markup by design, so neither route may rely on the
key being hard to guess. If you find a way to make either one answer for a node the site would not
serve to an anonymous visitor, that is a security bug.

**Nothing about a listener is stored.** No table, no migration, no IP, no user agent, no identity.
If you find a way to make this package answer "who listened", that is a security bug in this
project's terms even if nothing leaves the server.

**The cache is derived data on disk.** Keys are 64 hex characters and validated as such before
becoming a path, because they are file names. A key that is not hex is refused rather than
sanitised. Path traversal through a cache key would be a real finding.

**The speech endpoint is unsupported by Microsoft.** This is documented in the README rather than
hidden, along with the browser fallback. Reports that it is unofficial are not vulnerabilities;
that is a known and stated design trade. Reports that it leaks something are.

**This package holds no credentials.** There is one speech provider, the unofficial Edge endpoint,
and it takes no key. Azure Speech is not implemented in this version, so there is no credential
surface to expose. The `Provider` setting accepts only `"Edge"`; any other value stops the site at
startup.

## Supported versions

Fixes go to the latest published version. Umbraco 16, 17 and 18, with CI running against all three.
