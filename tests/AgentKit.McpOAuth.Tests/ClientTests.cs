using System.Net;
using static AgentKit.McpOAuth.Tests.OAuthDriver;

namespace AgentKit.McpOAuth.Tests;

/// <summary>Which clients get in: seeded loopback clients and client id metadata documents.</summary>
public sealed class ClientTests(OAuthApp app)
{
    [Theory]
    [InlineData("http://127.0.0.1:53682/callback")]
    [InlineData("http://localhost:4123/callback")]
    public async Task ClaudeCode_SeededClient_SignsInOnAnyLoopbackPort(string redirect)
    {
        var c = await ConnectAsync(app, app.Host, "claude-code", redirect);
        Assert.StartsWith(redirect, c.Location.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("claude-code", (await Json(await app.Host.McpAsync(c.AccessToken)))["client"]!.GetValue<string>());
    }

    [Fact]
    public async Task ALoopbackClient_IsNamedAsAProgramOnThisComputer()
    {
        using var http = app.Host.Browser();
        var html = await (await http.GetAsync(AuthorizeUrl("claude-code", "http://127.0.0.1:53682/callback", Pkce.New().Challenge, "l1"))).Content.ReadAsStringAsync();
        Assert.Contains("a program on this computer", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALoopbackClient_StillNeedsItsRegisteredHostAndPath()
    {
        using var http = app.Host.Browser();
        var wrongPath = await http.GetAsync(AuthorizeUrl("claude-code", "http://127.0.0.1:53682/elsewhere", Pkce.New().Challenge, "l2"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongPath.StatusCode);
        Assert.Null(wrongPath.Headers.Location);
        var https = await http.GetAsync(AuthorizeUrl("claude-code", "https://127.0.0.1:53682/callback", Pkce.New().Challenge, "l3"));
        Assert.Equal(HttpStatusCode.BadRequest, https.StatusCode);
    }

    [Fact]
    public async Task ClaudeCode_MetadataDocumentClient_SignsInOnALoopbackPort()
    {
        const string clientId = "https://claude.ai/oauth/claude-code-client-metadata";
        // As published (fetched 2026-11-11).
        app.MetadataDocuments[clientId] = """
            {"client_id":"https://claude.ai/oauth/claude-code-client-metadata","client_name":"Claude Code","client_uri":"https://claude.ai","redirect_uris":["http://localhost/callback","http://127.0.0.1/callback"],"grant_types":["authorization_code","refresh_token"],"response_types":["code"],"token_endpoint_auth_method":"none"}
            """;
        var c = await ConnectAsync(app, app.Host, clientId, "http://localhost:3118/callback");
        Assert.Equal(clientId, (await Json(await app.Host.McpAsync(c.AccessToken)))["client"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChatGpt_MetadataDocumentClient_FullFlow()
    {
        const string clientId = "https://chatgpt.com/oauth/client.json";
        const string redirect = "https://chatgpt.com/connector_platform_oauth_redirect";
        // As published (fetched 2026-11-11): ChatGPT prefers private_key_jwt but lists "none", the only method here.
        app.MetadataDocuments[clientId] = """
            {"client_id":"https://chatgpt.com/oauth/client.json","client_uri":"https://chatgpt.com/","redirect_uris":["https://chatgpt.com/connector_platform_oauth_redirect"],"token_endpoint_auth_method":"private_key_jwt","token_endpoint_auth_methods_supported":["none","private_key_jwt"],"grant_types":["authorization_code","refresh_token","urn:ietf:params:oauth:grant-type:jwt-bearer"],"response_types":["code"],"client_name":"ChatGPT","logo_uri":"https://persistent.oaistatic.com/sonic/misc/openai-logo.png","token_endpoint_auth_signing_alg":"RS256","jwks_uri":"https://chatgpt.com/oauth/jwks.json"}
            """;
        var c = await ConnectAsync(app, app.Host, clientId, redirect);
        Assert.Equal(OAuthApp.PublicUrl + "/", c.Iss);
        var who = await Json(await app.Host.McpAsync(c.AccessToken));
        Assert.Equal("chatgpt", who["agent"]!.GetValue<string>()); // the host's suggestion from the document's host
        Assert.Equal(clientId, app.Book.Find(c.ConnectionId)!.ClientId);
    }

    [Fact]
    public async Task MetadataDocumentClients_OutsideTheAllowlist_OrRedirectingOffTheirOrigin_AreRefused()
    {
        using var http = app.Host.Browser();
        var pkce = Pkce.New();

        // Not an allowlisted host: never fetched, so unknown.
        const string stranger = "https://evil.test/client.json";
        app.MetadataDocuments[stranger] = $$"""{"client_id":"{{stranger}}","redirect_uris":["https://evil.test/cb"]}""";
        var r1 = await http.GetAsync(AuthorizeUrl(stranger, "https://evil.test/cb", pkce.Challenge, "s7"));
        Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);
        Assert.Null(r1.Headers.Location);

        // An allowlisted host whose document sends the code somewhere else: refused, so the client stays unknown.
        const string offOrigin = "https://chatgpt.com/oauth/elsewhere/client.json";
        app.MetadataDocuments[offOrigin] = $$"""{"client_id":"{{offOrigin}}","redirect_uris":["https://evil.test/cb"]}""";
        var r2 = await http.GetAsync(AuthorizeUrl(offOrigin, "https://evil.test/cb", pkce.Challenge, "s8"));
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
        Assert.Null(r2.Headers.Location);

        // A document that names another URL as its client id: refused.
        const string impostor = "https://chatgpt.com/oauth/impostor.json";
        app.MetadataDocuments[impostor] = """{"client_id":"https://chatgpt.com/oauth/client.json","redirect_uris":["https://chatgpt.com/cb"]}""";
        var r3 = await http.GetAsync(AuthorizeUrl(impostor, "https://chatgpt.com/cb", pkce.Challenge, "s9"));
        Assert.Equal(HttpStatusCode.BadRequest, r3.StatusCode);
        Assert.Null(r3.Headers.Location);
    }
}
