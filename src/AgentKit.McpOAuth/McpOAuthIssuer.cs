namespace AgentKit.McpOAuth;

/// <summary>
/// The authorization server's public identity and every public URL derived from it. Registered as a singleton when
/// <see cref="McpOAuthOptions.PublicUrl"/> is set; absent from the container when the server is off.
/// </summary>
public sealed class McpOAuthIssuer
{
    private McpOAuthIssuer(Uri issuer)
    {
        Issuer = issuer;
        Base = issuer.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>
    /// Parses a public origin (an absolute http or https URL, optionally with a path, without query or fragment).
    /// Anything else is a misconfiguration and throws, so a wrong value fails at start rather than at the first sign-in.
    /// </summary>
    public static McpOAuthIssuer Parse(string publicUrl)
    {
        ArgumentNullException.ThrowIfNull(publicUrl);
        if (!Uri.TryCreate(publicUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"The MCP OAuth public URL must be the server's public origin, such as https://mcp.example.com (got '{publicUrl}').");
        // OpenIddict states the issuer with a trailing slash, and discovery's "issuer" must equal the protected resource
        // metadata's authorization_servers[0] character for character (Claude and ChatGPT both compare).
        return new McpOAuthIssuer(new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"));
    }

    /// <summary>The issuer as OpenIddict states it: the public origin with a trailing slash.</summary>
    public Uri Issuer { get; }

    /// <summary>The issuer without its trailing slash, the prefix of every derived URL.</summary>
    public string Base { get; }

    /// <summary>The MCP resource (RFC 8707): the URL clients connect to and every access token's audience, <c>&lt;issuer&gt;/mcp</c>.</summary>
    public string ResourceUrl => Base + McpOAuthDefaults.ResourcePath;

    /// <summary>The RFC 9728 protected resource metadata URL, named by every <c>/mcp</c> 401.</summary>
    public string ProtectedResourceMetadataUrl => Base + "/.well-known/oauth-protected-resource";

    /// <summary>Whether the issuer is plain http (local development only); OpenIddict's https requirement is then lifted.</summary>
    public bool AllowsHttp => Issuer.Scheme == Uri.UriSchemeHttp;
}

/// <summary>The fixed paths and names the package serves and uses.</summary>
public static class McpOAuthDefaults
{
    /// <summary>The MCP endpoint, the protected resource: <c>/mcp</c>.</summary>
    public const string ResourcePath = "/mcp";

    /// <summary>The authorization endpoint and consent page: <c>/oauth/authorize</c>.</summary>
    public const string AuthorizePath = "/oauth/authorize";

    /// <summary>The token endpoint: <c>/oauth/token</c>.</summary>
    public const string TokenPath = "/oauth/token";

    /// <summary>
    /// The named <see cref="System.Net.Http.HttpClient"/> that fetches client id metadata documents (no redirects, no
    /// cookies). A test host can replace its primary handler to serve documents without the network.
    /// </summary>
    public const string MetadataHttpClientName = "AgentKit.McpOAuth.Metadata";
}
