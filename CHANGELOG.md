# Changelog

Per package, newest first. Versions are `0.1.<CI run number>`; an entry names the first version that carries it.

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
