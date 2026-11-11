# AgentKit.McpOAuth

An OAuth 2.1 authorization server for a remote MCP server, built on [OpenIddict](https://openiddict.com). Hosted
connectors (claude.ai, ChatGPT, Claude Code) sign in with it, and each sign-in becomes one of your connections.

The package handles the protocol. Your app stores the connections and decides who the owner is.

## What it does

- **Authorization code with PKCE S256 only.** `plain` is refused, and discovery lists S256 alone. Every client is
  public (`token_endpoint_auth_methods_supported: ["none"]`).
- **Opaque reference tokens, stored hashed.**
  - Access tokens last 60 minutes and refresh tokens 30 days.
  - Each refresh rotates the refresh token. Reusing an old one revokes the whole grant.
- **Audience pinned to `<issuer>/mcp`.** RFC 8707 `resource` indicators are accepted at both endpoints. Naming any
  other resource gets `invalid_target`. A token minted for another origin is refused.
- **Discovery.**
  - RFC 9728 protected resource metadata at `/.well-known/oauth-protected-resource` and
    `/.well-known/oauth-protected-resource/mcp`.
  - RFC 8414 metadata at `/.well-known/oauth-authorization-server`.
  - Every `/mcp` 401 carries `WWW-Authenticate: Bearer … resource_metadata="…"`.
  - RFC 9207: every authorization response includes `iss`.
- **Clients.**
  - Seeded clients you list. A loopback redirect matches on any port (RFC 8252).
  - Client ID Metadata Documents from hosts you allowlist. A document's redirects must stay on its own origin or a
    loopback address.
  - No dynamic client registration.
- **Consent.**
  - A server-rendered page: no scripts, never framed, `no-store`.
  - Consent counts only as a POSTed `consent=allow` that carries the page's own unlock proof. The unlock is an HMAC
    over the authorize request, the owner id and an expiry. A crafted link or a cross-site post never grants.
- **Per-call revocation.**
  - Every `/mcp` call reads the token and its grant from the database.
  - Your store's `ResolveAsync` then reads your connection. Nothing is cached.
  - Revoking either one refuses the very next call.
- **Issuer pinning behind a proxy.** Scheme, host and path base come from `PublicUrl` on `/.well-known`, `/oauth` and
  `/mcp`, whatever arrived. No `X-Forwarded-*` header is trusted.
- **Keys in the database.** The signing and encryption keys are created on first use in `oauth_key`. Redeploys and
  replicas keep every connection signed in.

## Use it

```csharp
builder.Services.AddMcpOAuth(o =>
{
    o.PublicUrl = builder.Configuration["PublicUrl"];        // unset: OAuth off, your own bearers only
    o.ConnectionString = builder.Configuration.GetConnectionString("App")!;
    o.Scope = "tasks";
    o.ResourceName = "My Server";
    o.ConsentDescription = "It will read and write tasks as the agent you pick.";
    o.Clients.Add(McpOAuthClient.ClaudeAi);                  // claude.ai with client id "claude-ai"
    o.Clients.Add(McpOAuthClient.ClaudeCode);                // claude mcp add … --client-id claude-code
    o.MetadataDocumentHosts.Add("claude.ai");                // Claude Code's metadata document
    o.MetadataDocumentHosts.Add("chatgpt.com");              // ChatGPT's
});
builder.Services.AddScoped<IMcpConnectionStore, MyConnections>();
builder.Services.AddSingleton<IMcpConsentOwner>(
    new McpSecretOwnerCheck(() => builder.Configuration["OwnerToken"]));   // interim; see below
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseMcpOAuth();          // issuer pinning and metadata documents, before anything reads the request
app.UseAuthentication();    // OpenIddict's token, discovery and JWKS endpoints run here
app.UseMcpOAuthGate();      // /mcp: bearer → your connection as HttpContext.User, or 401 with resource_metadata
app.UseAuthorization();
app.MapMcpOAuth();          // protected resource metadata and the consent page
app.MapMcp("/mcp");         // your MCP server
```

The MCP endpoint is always `/mcp`. The authorization and token endpoints are always `/oauth/authorize` and
`/oauth/token`.

### What you implement

**`IMcpConnectionStore`**: your connections.

| Method | When | What it does |
| --- | --- | --- |
| `RenderChoicesAsync` | consent, after the owner is proven | Returns your form fields as an HTML fragment (which agent, which projects, a label), HTML-encoded. |
| `CreateAsync` | the owner allowed | Creates the connection with id `grant.ConnectionId`, bound to `grant.AuthorizationId`. Throw `McpConsentRejectedException` to show the page again with your message; the grant is then revoked and no code is issued. |
| `ResolveAsync` | every `/mcp` call with an OAuth token | Reads the connection, uncached. Returns its principal, or null if it is revoked or gone. |
| `ResolveOtherBearerAsync` | every `/mcp` call, first | Your own bearer (a static token, say), or null. The default accepts nothing. |

**`IMcpConsentOwner`**: who may consent. It answers one of three:

- `McpOwnerCheck.Owner(id)`: the owner is proven.
- `AskForCredential(fieldsHtml, error)`: show the first step with your fields.
- `SignIn(url)`: redirect, for example to your sign-in page, returning to `request.ReturnUrl`.

`McpSecretOwnerCheck` is a ready one: the owner types a shared secret once, compared in constant time and never echoed
back. Replace it with a check of your owner's session when you have sign-in. Allow still needs the page's unlock, so a
session cookie alone never grants.

**Revoking.** Revoke your connection first, then call `IMcpOAuthGrants.RevokeAsync(authorizationId)`. That revokes the
grant and every token under it, so the client cannot refresh its way back in. `IMcpOAuthGrants` is registered even with
`PublicUrl` unset, so a CLI can revoke too.

**Your own gate.** If you write your own `/mcp` gate, call `HttpContext.AuthenticateMcpOAuthAsync()` to get the grant,
then `WriteMcpOAuthChallengeAsync(presented)` for the 401.

### Schema

The package never migrates. Run `McpOAuthSchema.ReadScript()` once with your own migrator. It creates OpenIddict's four
tables and `oauth_key`, all `IF NOT EXISTS`. No EF migrations are involved. Apply it before the host starts:
the seeded clients are written at start. The role your app connects as needs SELECT,
INSERT, UPDATE and DELETE on `McpOAuthSchema.Tables`.

### Operational notes

- **Changing `PublicUrl` signs every connection out.** It is the issuer and the audience.
- **Rotating keys.** Delete the `oauth_key` rows. Every connection then signs in again.
- **Logging.** OpenIddict logs authorization request parameters at Information, and a consent POST can carry an owner
  secret. The package sets the `OpenIddict` log category to Warning. Do not lower it in configuration.
- **TLS** is your proxy's job. `PublicUrl` may be `http://` only for local development.
