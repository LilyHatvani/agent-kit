# agent-kit

Shared building blocks for products that agents connect to: an MCP authorization server, owner sign-in, PostgreSQL
helpers, storage signing, and the tooling around them. MIT licensed. The packages publish to nuget.org and npm.

Each package is extracted from code that already runs in production, stripped of that product's specifics behind
small interfaces the host implements.

## Packages

| Package | What | Status |
| --- | --- | --- |
| NuGet [`AgentKit.McpOAuth`](src/AgentKit.McpOAuth/README.md) | MCP authorization server on OpenIddict. PKCE S256, hashed reference tokens, audience pinning, RFC 9728 discovery, a POST-only consent gate, uncached per-call revocation, issuer pinning behind a proxy | **built** |
| NuGet `AgentKit.ProviderSignIn` | Server-driven Google and Discord sign-in. Single-use state and nonce, an ID token check, binding by provider account id (never email) | planned |
| NuGet `AgentKit.Postgres` | Append-only guard, worker-cursor leases, a run-once migrator | planned |
| NuGet `AgentKit.Storage` | S3 SigV4 query presigner that signs Content-Length | planned |
| npm `@agentkit/tooling` | Railway CLI wrapper, a ship-gate core, bundle budgets, a theme contrast check | planned |
| npm `@agentkit/tokens` | Design tokens, focus rings, reduced motion | planned |

## AgentKit.McpOAuth in short

```csharp
builder.Services.AddMcpOAuth(o =>
{
    o.PublicUrl = "https://mcp.example.com";      // issuer; <it>/mcp is every token's audience
    o.ConnectionString = connectionString;        // PostgreSQL, schema from McpOAuthSchema.ReadScript()
    o.Scope = "tasks";
    o.Clients.Add(McpOAuthClient.ClaudeAi);
    o.MetadataDocumentHosts.Add("chatgpt.com");
});
builder.Services.AddScoped<IMcpConnectionStore, MyConnections>();   // create, resolve (uncached), your choices
builder.Services.AddSingleton<IMcpConsentOwner, MyOwnerCheck>();    // who may consent

app.UseMcpOAuth();
app.UseAuthentication();
app.UseMcpOAuthGate();
app.UseAuthorization();
app.MapMcpOAuth();
```

The [package README](src/AgentKit.McpOAuth/README.md) covers the full contract: what the host implements, the schema,
revocation and operational notes.

## Develop

- **Requirements.** The .NET 10 SDK and a local PostgreSQL.
- **Tests.** The tests create a throwaway database per run through an admin connection:
  `AGENTKIT_TEST_ADMIN_CONNECTION`, default `Host=localhost;Username=postgres;Database=postgres` (password from pgpass).

```bash
dotnet build AgentKit.slnx
```

```bash
dotnet test --project tests/AgentKit.McpOAuth.Tests
```

The tests are blackbox: a minimal host uses the package exactly as a product would, and every assertion goes through
HTTP.

## Versions and publishing

- **Versions.** Nobody bumps versions by hand. CI builds `0.1.<run number>`, and a local build is `0.1.0-dev`.
- **CI.** Every push and pull request builds, tests against PostgreSQL 18 and packs.
- **Publishing.** A push to `develop` publishes to nuget.org through Trusted Publishing: no API key is stored; nuget.org
  trusts this repository's `ci.yml` through GitHub's OIDC token. It runs when the repository variable `NUGET_USER`
  (the nuget.org profile name) is set; otherwise the publish job is skipped and the packages stay attached to the run.
- **Changes.** [CHANGELOG.md](CHANGELOG.md) lists them per package.
