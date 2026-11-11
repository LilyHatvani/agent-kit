namespace AgentKit.McpOAuth;

/// <summary>
/// What the host tells the authorization server at registration (<see cref="McpOAuthServiceCollectionExtensions.AddMcpOAuth"/>).
/// Read once, when the services are registered: OpenIddict needs the issuer, audience and lifetimes then.
/// </summary>
public sealed class McpOAuthOptions
{
    /// <summary>
    /// The server's public origin, such as <c>https://mcp.example.com</c>: the issuer, and the base of every public URL
    /// (the MCP resource <c>&lt;issuer&gt;/mcp</c> is every access token's audience). Always configuration, never the
    /// request's Host. Null or blank turns the authorization server off: the stores and
    /// <see cref="IMcpOAuthGrants"/> stay registered (revocation still works), discovery and consent are not mapped, and
    /// the gate accepts only what <see cref="IMcpConnectionStore.ResolveOtherBearerAsync"/> accepts.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>The PostgreSQL connection string for OpenIddict's stores and the key table (<see cref="McpOAuthSchema"/>).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The one scope every connection is granted, also listed in the protected resource metadata. Default <c>mcp</c>.</summary>
    public string Scope { get; set; } = "mcp";

    /// <summary>The product's name: the protected resource metadata's <c>resource_name</c> and the consent page's heading.</summary>
    public string ResourceName { get; set; } = "this MCP server";

    /// <summary>
    /// Plain text under the consent page's heading: what a connection may do. HTML-encoded when shown. Null shows nothing.
    /// </summary>
    public string? ConsentDescription { get; set; }

    /// <summary>
    /// Clients registered at start (created or refreshed, never removed). Every client is public and must use PKCE S256.
    /// A loopback redirect (<c>http://127.0.0.1/…</c>, <c>http://localhost/…</c>) matches on any port (RFC 8252 § 7.3).
    /// </summary>
    public IList<McpOAuthClient> Clients { get; } = [];

    /// <summary>
    /// Hosts whose client id metadata documents are trusted (exact host match, https only), such as <c>claude.ai</c>
    /// and <c>chatgpt.com</c>. A client id that is an https URL on one of them is fetched on each authorize request
    /// and stored as an ordinary public client; every redirect it names must share its origin or be loopback. Empty
    /// turns metadata documents off, and discovery then does not advertise them. There is no dynamic registration.
    /// </summary>
    public IList<string> MetadataDocumentHosts { get; } = [];

    /// <summary>Access token lifetime. Default 60 minutes.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Refresh token lifetime. Default 30 days. Each refresh rotates it; a reused one revokes the grant.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Authorization code lifetime. Default 2 minutes.</summary>
    public TimeSpan AuthorizationCodeLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long the consent page's second step stays valid after the owner was proven. Default 15 minutes.</summary>
    public TimeSpan ConsentLifetime { get; set; } = TimeSpan.FromMinutes(15);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("McpOAuthOptions.ConnectionString is required.");
        if (string.IsNullOrWhiteSpace(Scope) || Scope.Any(char.IsWhiteSpace) || Scope == "offline_access")
            throw new InvalidOperationException("McpOAuthOptions.Scope must be one scope name without spaces.");
        foreach (var lifetime in new[] { AccessTokenLifetime, RefreshTokenLifetime, AuthorizationCodeLifetime, ConsentLifetime })
            if (lifetime <= TimeSpan.Zero) throw new InvalidOperationException("McpOAuthOptions lifetimes must be positive.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var client in Clients)
        {
            if (string.IsNullOrWhiteSpace(client.ClientId) || !ids.Add(client.ClientId))
                throw new InvalidOperationException($"McpOAuthOptions.Clients: client id '{client.ClientId}' is empty or listed twice.");
            if (client.RedirectUris.Count == 0)
                throw new InvalidOperationException($"McpOAuthOptions.Clients: '{client.ClientId}' names no redirect.");
            foreach (var r in client.RedirectUris)
                if (!Uri.TryCreate(r, UriKind.Absolute, out _))
                    throw new InvalidOperationException($"McpOAuthOptions.Clients: '{client.ClientId}' redirect '{r}' is not an absolute URL.");
        }
        foreach (var host in MetadataDocumentHosts)
            if (Uri.CheckHostName(host) is UriHostNameType.Unknown or UriHostNameType.Basic)
                throw new InvalidOperationException($"McpOAuthOptions.MetadataDocumentHosts: '{host}' is not a host name.");
    }
}

/// <summary>A client registered at start (<see cref="McpOAuthOptions.Clients"/>): public, PKCE S256, these redirects only.</summary>
/// <param name="ClientId">The <c>client_id</c> the client sends.</param>
/// <param name="DisplayName">The name the consent page shows.</param>
/// <param name="RedirectUris">Allowed redirects, compared exactly, except that a loopback redirect matches on any port.</param>
public sealed record McpOAuthClient(string ClientId, string DisplayName, IReadOnlyList<string> RedirectUris)
{
    /// <summary>
    /// A claude.ai custom connector whose Advanced settings name the client id <c>claude-ai</c>
    /// (redirect <c>https://claude.ai/api/mcp/auth_callback</c>).
    /// </summary>
    public static McpOAuthClient ClaudeAi { get; } = new("claude-ai", "claude.ai", ["https://claude.ai/api/mcp/auth_callback"]);

    /// <summary>
    /// Claude Code with <c>--client-id claude-code</c>: a loopback redirect on any port. Recent Claude Code versions
    /// present a metadata document from <c>claude.ai</c> instead (<see cref="McpOAuthOptions.MetadataDocumentHosts"/>).
    /// </summary>
    public static McpOAuthClient ClaudeCode { get; } = new("claude-code", "Claude Code", ["http://127.0.0.1/callback", "http://localhost/callback"]);
}
