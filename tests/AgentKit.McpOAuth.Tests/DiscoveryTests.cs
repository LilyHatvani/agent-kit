using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static AgentKit.McpOAuth.Tests.OAuthDriver;

namespace AgentKit.McpOAuth.Tests;

/// <summary>How a client finds the authorization server, and the host with the server off.</summary>
public sealed class DiscoveryTests(OAuthApp app)
{
    [Fact]
    public async Task Discovery_FromTheMcp401_LeadsToAnAuthorizationServerMcpClientsAccept()
    {
        using var http = app.Host.Browser();
        var challenge = await app.Host.McpAsync(bearer: null);
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        var www = challenge.Headers.WwwAuthenticate.ToString();
        Assert.DoesNotContain("invalid_token", www, StringComparison.Ordinal); // nothing was presented
        var metadataUrl = Regex.Match(www, "resource_metadata=\"([^\"]+)\"").Groups[1].Value;
        Assert.Equal(OAuthApp.PublicUrl + "/.well-known/oauth-protected-resource", metadataUrl);

        var prm = await Json(await http.GetAsync(new Uri(metadataUrl).PathAndQuery));
        Assert.Equal(OAuthApp.Resource, prm["resource"]!.GetValue<string>());
        Assert.Equal([OAuthApp.Scope], Strings(prm["scopes_supported"]));
        Assert.Equal("Test Host", prm["resource_name"]!.GetValue<string>());
        var issuer = prm["authorization_servers"]![0]!.GetValue<string>();
        Assert.Equal(prm.ToJsonString(), (await Json(await http.GetAsync("/.well-known/oauth-protected-resource/mcp"))).ToJsonString());

        var meta = await Json(await http.GetAsync(new Uri(issuer.TrimEnd('/') + "/.well-known/oauth-authorization-server").PathAndQuery));
        // Clients compare these character for character, and every URL is the configured origin, not the TestServer's.
        Assert.Equal(issuer, meta["issuer"]!.GetValue<string>());
        Assert.Equal(OAuthApp.PublicUrl + "/", issuer);
        Assert.Equal(OAuthApp.PublicUrl + "/oauth/authorize", meta["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal(OAuthApp.PublicUrl + "/oauth/token", meta["token_endpoint"]!.GetValue<string>());
        Assert.Equal(["S256"], Strings(meta["code_challenge_methods_supported"]));
        Assert.Equal(["none"], Strings(meta["token_endpoint_auth_methods_supported"]));
        Assert.True(meta["client_id_metadata_document_supported"]!.GetValue<bool>());
        Assert.True(meta["authorization_response_iss_parameter_supported"]!.GetValue<bool>());
        Assert.Null(meta["registration_endpoint"]);
    }

    [Fact]
    public async Task WithoutAPublicUrl_OAuthIsOff_AndTheHostsOwnBearersStillConnect()
    {
        await using var off = await app.StartHostAsync(publicUrl: null);
        var secret = app.Book.MintStatic("claude");
        var ok = await off.McpAsync(secret);
        Assert.Equal("claude", (await Json(ok))["agent"]!.GetValue<string>());

        var opaque = await off.McpAsync("an-opaque-token-that-is-not-the-hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, opaque.StatusCode);
        Assert.Contains("invalid_token", opaque.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("resource_metadata", opaque.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        using var http = off.Browser();
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/.well-known/oauth-protected-resource")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/.well-known/oauth-authorization-server")).StatusCode);
    }

    [Fact]
    public async Task AStaticBearer_ConnectsOnTheOAuthHostToo_UntilTheHostRevokesIt()
    {
        var secret = app.Book.MintStatic("chatgpt");
        Assert.Equal("chatgpt", (await Json(await app.Host.McpAsync(secret)))["agent"]!.GetValue<string>());

        var id = Guid.Parse((await Json(await app.Host.McpAsync(secret)))["connection"]!.GetValue<string>());
        using var http = app.Host.Browser();
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/connections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Host.McpAsync(secret)).StatusCode);
    }

    [Fact]
    public void Schema_ShipsTheOpenIddictTablesAndTheKeyTable()
    {
        var sql = McpOAuthSchema.ReadScript();
        foreach (var table in McpOAuthSchema.Tables)
            Assert.Contains("CREATE TABLE IF NOT EXISTS " + table, sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcp.example.com")]
    [InlineData("ftp://mcp.example.com")]
    [InlineData("https://mcp.example.com/?x=1")]
    public void AMalformedPublicUrl_FailsAtRegistration(string publicUrl)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddMcpOAuth(o =>
        {
            o.PublicUrl = publicUrl;
            o.ConnectionString = app.ConnectionString;
        }));
    }

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
}
