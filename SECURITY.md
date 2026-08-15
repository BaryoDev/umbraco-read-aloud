# Security

## Reporting

**Please do not open a public issue.** Use
[GitHub's private vulnerability reporting](https://github.com/BaryoDev/umbraco-read-aloud/security/advisories/new),
or the contact route in the [BaryoDev policy](https://github.com/BaryoDev/.github/blob/main/SECURITY.md).

## What this package touches

**One anonymous endpoint exists.** `GET /read-aloud/{nodeKey}` is unauthenticated, because every
visitor's browser calls it. It is worth knowing what it will and will not accept:

- **It takes no text.** The server reads the configured property itself. There is no
  arbitrary-text endpoint here and therefore no abuse surface for one.
- It is capped by `MaxChars` and rate limited per IP.
- `AllowedVoices` restricts what a caller may request; anything else falls back to the default.
- It serves audio only for **published** nodes, so unpublished content cannot be read out.

**Nothing about a listener is stored.** No table, no migration, no IP, no user agent, no identity.
If you find a way to make this package answer "who listened", that is a security bug in this
project's terms even if nothing leaves the server.

**The cache is derived data on disk.** Keys are 64 hex characters and validated as such before
becoming a path, because they are file names. A key that is not hex is refused rather than
sanitised. Path traversal through a cache key would be a real finding.

**The speech endpoint is unsupported by Microsoft.** This is documented in the README rather than
hidden, along with the two fallbacks. Reports that it is unofficial are not vulnerabilities; that
is a known and stated design trade. Reports that it leaks something, or that credentials for the
Azure Speech provider are exposed, are.

## Supported versions

Fixes go to the latest published version. Umbraco 16, 17 and 18, with CI running against all three.
