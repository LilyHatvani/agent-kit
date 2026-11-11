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
