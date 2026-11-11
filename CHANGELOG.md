# Changelog

Per package, newest first. Versions are `0.1.<CI run number>`; an entry names the first version that carries it.

## AgentKit.ProviderSignIn

### Unreleased

- **First release.** Server-driven Google and Discord sign-in, the server as relying party:
  - authorization code with PKCE S256, and one registered redirect per provider, built from `PublicUrl`;
  - a single-use state (10 min, stored hashed), bound to the browser by a `__Host-` cookie, and pinned to its provider;
  - Google's ID token checked for RS256 signature, issuer, audience, expiry and this sign-in's nonce; Discord's user id
    read from `/users/@me`;
  - no provider tokens kept;
  - each provider enabled only with its client id and secret, and listed at `GET /api/auth/providers`.
- **Host contract.** The host implements `IProviderSignInCompletion` and receives a verified `(provider, account id)`
  plus display hints. It decides who that is; the package never matches by email. `ProviderSignInFlow.BeginAsync` lets
  the host begin a sign-in with its own purpose (setup, bind), kept server-side.
- **State store.** In memory by default, or PostgreSQL (`ConnectionString`, schema from `ProviderSignInSchema.ReadScript()`).

## AgentKit.McpOAuth

### Unreleased

Fixes from Relay's security review of 2026-11-11. No schema change and no option is required; behaviour changes are
marked.

- **Loopback redirects are matched on their literal host.** An `http` redirect counts as loopback (any port) only when
  written as `127.0.0.1`, `[::1]` or `localhost` (RFC 8252 § 8.3), with no userinfo, whitespace or control characters,
  for seeded clients and metadata documents alike. Before, anything .NET maps to loopback passed, including
  `http://loopback:8080/…`, a name the browser looks up. *Behaviour change:* `127.1`, `localhost.` and `user@localhost`
  redirects are now refused.
- **A metadata document client id must be canonical.** It is fetched and stored only when `client_id ==
  url.AbsoluteUri`; any other URL-shaped client id (`:443`, dot segments, upper case) gets 400 at `/oauth/authorize`,
  so a row an earlier version stored under an alias is never used.
- **Metadata documents are cached.** A stored document is not fetched again for `MetadataDocumentCacheLifetime`
  (5 min), a missing or refused one for `MetadataDocumentFailureCacheLifetime` (1 min); at most
  `MetadataDocumentCacheSize` (1000) URLs, oldest out first. Per process.
- **Optional rate limit on `/oauth/authorize`.** `AuthorizeRateLimiter` takes a `PartitionedRateLimiter<HttpContext>`,
  applied to GET and POST before any fetch or owner check; refused requests get 429 with `Retry-After`.
- **The consent unlock is single-use and bound to the browser.** Allow spends the unlock's nonce in the token table's
  unique `ReferenceId` index, so a double submit or replay (on any replica) gets the page again instead of a second
  grant. The unlock is also bound to a random `__Host-agentkit-mcp-consent` cookie (HttpOnly, SameSite=Strict) the page
  sets, and its MAC now covers `scope`, `response_type` and `code_challenge_method`, over a length-prefixed encoding
  instead of `\n`-joined fields. *Behaviour change:* the consent page needs cookies, and a consent page open across
  the upgrade must be confirmed again.
- **`McpSecretOwnerCheck` limits wrong secrets.** 10 per remote address per 15 minutes (`MaxFailedAttempts`,
  `FailedAttemptWindow`); past that every attempt from the address is refused until the window ends. *Behaviour
  change, on by default:* behind a proxy without a forwarded-headers middleware every request shares the proxy's
  address, so anyone can hold the owner's consent page shut with 10 wrong posts per 15 minutes (existing connections
  are unaffected). Set `MaxFailedAttempts = int.MaxValue` to turn it off.

### 0.1.4

- **First release.** An MCP authorization server on OpenIddict 7.8:
  - authorization code with PKCE S256 only, for public clients;
  - hashed reference access tokens (60 min) and refresh tokens (30 days), with strict rotation: reusing a refresh token
    revokes the grant;
  - audience pinned to `<issuer>/mcp`, and RFC 8707 resource indicators checked at both endpoints;
  - RFC 9728 discovery, RFC 9207 `iss`, and `WWW-Authenticate` with `resource_metadata`;
  - seeded clients with any-port loopback redirects, and client ID metadata documents from allowlisted hosts;
  - a POST-only consent page with an HMAC unlock;
  - uncached per-call revocation;
  - issuer pinning behind a proxy;
  - keys stored in the database.
- **Host contracts.** The host implements `IMcpConnectionStore` and `IMcpConsentOwner`. `McpSecretOwnerCheck` is a
  ready owner check by shared secret.
- **Schema.** Shipped as an embedded script (`McpOAuthSchema.ReadScript()`) for the host's own migrator.
