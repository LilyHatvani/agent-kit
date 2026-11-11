using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json.Nodes;
using static AgentKit.McpOAuth.Tests.OAuthDriver;

namespace AgentKit.McpOAuth.Tests;

/// <summary>The token endpoint, refresh, revocation and the audience.</summary>
public sealed class TokenTests(OAuthApp app)
{
    [Fact]
    public async Task IssuedTokens_AreOpaqueReferences_StoredOnlyByHash()
    {
        var c = await ConnectAsync(app, app.Host, "claude-ai", ClaudeAiCallback);
        foreach (var token in new[] { c.AccessToken, c.RefreshToken })
        {
            Assert.DoesNotContain(".", token, StringComparison.Ordinal); // not a JWT: nothing to decode or verify offline
            await using var conn = new Npgsql.NpgsqlConnection(app.ConnectionString);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new Npgsql.NpgsqlCommand("""
                SELECT count(*) FROM "OpenIddictTokens"
                WHERE "ReferenceId" = @t OR "Payload" LIKE '%' || @t || '%' OR "Id" = @t
                """, conn);
            cmd.Parameters.AddWithValue("t", token);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
    }

    [Fact]
    public async Task TokenExchange_WithTheWrongVerifier_IsRefused()
    {
        using var http = app.Host.Browser();
        var pkce = Pkce.New();
        var approved = await ApproveAsync(app, http, "claude-ai", ClaudeAiCallback, pkce, new() { ["label"] = NewLabel() });
        var r = await http.PostAsync("/oauth/token", Form(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = approved.Code, ["redirect_uri"] = ClaudeAiCallback,
            ["client_id"] = "claude-ai", ["code_verifier"] = Pkce.New().Verifier, ["resource"] = OAuthApp.Resource,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_grant", JsonNode.Parse(await r.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Refresh_RotatesTokens_AndReusingAnOldRefreshTokenRevokesTheWholeGrant()
    {
        var c = await ConnectAsync(app, app.Host, "claude-ai", ClaudeAiCallback);
        using var http = app.Host.Browser();

        var refreshed = await RefreshAsync(http, c.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        var access2 = refreshed.Json["access_token"]!.GetValue<string>();
        var refresh2 = refreshed.Json["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(c.RefreshToken, refresh2);
        Assert.Equal(HttpStatusCode.OK, (await app.Host.McpAsync(access2)).StatusCode);

        // The first refresh token again: someone else holds a copy. The grant dies with everything issued under it.
        var replay = await RefreshAsync(http, c.RefreshToken);
        Assert.Equal("invalid_grant", replay.Json["error"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Host.McpAsync(access2)).StatusCode);
        Assert.Equal("invalid_grant", (await RefreshAsync(http, refresh2)).Json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task RevokingTheConnection_FailsTheNextCall_AndItsRefreshToken()
    {
        var c = await ConnectAsync(app, app.Host, "claude-ai", ClaudeAiCallback);
        Assert.Equal(HttpStatusCode.OK, (await app.Host.McpAsync(c.AccessToken)).StatusCode);

        using var http = app.Host.Browser();
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/connections/{c.ConnectionId}")).StatusCode);

        var next = await app.Host.McpAsync(c.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, next.StatusCode);
        var www = next.Headers.WwwAuthenticate.ToString();
        Assert.Contains("error=\"invalid_token\"", www, StringComparison.Ordinal);
        Assert.Contains("resource_metadata=\"" + OAuthApp.PublicUrl + "/.well-known/oauth-protected-resource\"", www, StringComparison.Ordinal);
        Assert.Equal("invalid_grant", (await RefreshAsync(http, c.RefreshToken)).Json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task RevokingOnlyTheGrant_FailsTheNextCall_EvenWhileTheHostsConnectionIsLive()
    {
        var c = await ConnectAsync(app, app.Host, "claude-ai", ClaudeAiCallback);
        Assert.Equal(HttpStatusCode.OK, (await app.Host.McpAsync(c.AccessToken)).StatusCode);

        var grants = new ServiceCollection()
            .AddMcpOAuth(o => o.ConnectionString = app.ConnectionString)
            .BuildServiceProvider();
        await using (grants)
        {
            // The stores alone, with the server off, as a host's CLI would use them.
            await grants.GetRequiredService<IMcpOAuthGrants>()
                .RevokeAsync(app.Book.Find(c.ConnectionId)!.AuthorizationId!, TestContext.Current.CancellationToken);
        }

        Assert.False(app.Book.Find(c.ConnectionId)!.Revoked);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Host.McpAsync(c.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task AnAccessTokenMintedForAnotherOriginAndResource_IsRefused()
    {
        var c = await ConnectAsync(app, app.Host, "claude-ai", ClaudeAiCallback);

        // The same database and keys behind another public origin: a token whose audience is mcp.test/mcp is not for
        // other.test/mcp, however valid it is otherwise.
        await using var other = await app.StartHostAsync("https://other.test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.McpAsync(c.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Host.McpAsync(c.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task ARedeployOrSecondReplica_OnTheSameDatabase_KeepsTheConnectionSignedIn()
    {
        var c = await ConnectAsync(app, app.Host, "claude-ai", ClaudeAiCallback);

        // A fresh process, same origin, same database: the keys come from the database, not memory.
        await using var replica = await app.StartHostAsync(OAuthApp.PublicUrl);
        Assert.Equal(HttpStatusCode.OK, (await replica.McpAsync(c.AccessToken)).StatusCode);
        using var http = replica.Browser();
        var refreshed = await RefreshAsync(http, c.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal(HttpStatusCode.OK, (await replica.McpAsync(refreshed.Json["access_token"]!.GetValue<string>())).StatusCode);
    }

    [Fact]
    public async Task AConsentStartedOnOneReplica_CompletesOnAnother()
    {
        await using var replica = await app.StartHostAsync(OAuthApp.PublicUrl);
        using var first = app.Host.Browser();
        using var second = replica.Browser();
        var pkce = Pkce.New();
        var hidden = Hidden(await (await first.GetAsync(AuthorizeUrl("claude-ai", ClaudeAiCallback, pkce.Challenge, "r1"))).Content.ReadAsStringAsync());
        var unlockedHtml = await (await Post(first, hidden, new() { ["password"] = app.OwnerSecret, ["consent"] = "continue" })).Content.ReadAsStringAsync();

        var approved = await AllowAsync(second, unlockedHtml, ClaudeAiCallback, "r1", new() { ["label"] = NewLabel() });
        Assert.NotEmpty(approved.Code);
    }

    [Fact]
    public async Task TokenRequests_NamingAnotherResource_GetNoTokenForIt()
    {
        using var http = app.Host.Browser();
        var pkce = Pkce.New();
        var approved = await ApproveAsync(app, http, "claude-ai", ClaudeAiCallback, pkce, new() { ["label"] = NewLabel() });
        var exchange = await http.PostAsync("/oauth/token", Form(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = approved.Code, ["redirect_uri"] = ClaudeAiCallback,
            ["client_id"] = "claude-ai", ["code_verifier"] = pkce.Verifier, ["resource"] = "https://evil.test/mcp",
        }));
        var body = JsonNode.Parse(await exchange.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(HttpStatusCode.BadRequest, exchange.StatusCode);
        Assert.Equal("invalid_target", body["error"]!.GetValue<string>());
        Assert.Null(body["access_token"]);

        var c = await ConnectAsync(app, app.Host, "claude-ai", ClaudeAiCallback);
        Assert.Equal("invalid_target", (await RefreshAsync(http, c.RefreshToken, resource: "https://evil.test/mcp")).Json["error"]!.GetValue<string>());
    }
}
