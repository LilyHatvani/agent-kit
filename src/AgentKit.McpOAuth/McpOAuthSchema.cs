namespace AgentKit.McpOAuth;

/// <summary>
/// The package's PostgreSQL schema: OpenIddict's four tables and <c>oauth_key</c>. The package never migrates; the host
/// runs <see cref="ReadScript"/> once with its own migrator (as a numbered migration of its own, say). Every statement is
/// <c>IF NOT EXISTS</c>. The role the app connects as needs SELECT, INSERT, UPDATE and DELETE on these tables.
/// </summary>
public static class McpOAuthSchema
{
    /// <summary>The embedded resource's name in the package assembly.</summary>
    public const string ResourceName = "AgentKit.McpOAuth.Schema.mcp-oauth.sql";

    /// <summary>The tables the schema creates, for a host that grants per table.</summary>
    public static IReadOnlyList<string> Tables { get; } =
        ["\"OpenIddictApplications\"", "\"OpenIddictScopes\"", "\"OpenIddictAuthorizations\"", "\"OpenIddictTokens\"", "oauth_key"];

    /// <summary>The schema script (PostgreSQL), as shipped in this package version.</summary>
    public static string ReadScript()
    {
        using var stream = typeof(McpOAuthSchema).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
