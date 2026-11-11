using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using static AgentKit.McpOAuth.Tests.OAuthDriver;

namespace AgentKit.McpOAuth.Tests;

/// <summary>The consent page: who may consent, what counts as consent, and what the host gets.</summary>
public sealed class ConsentTests(OAuthApp app)
{
    [Fact]
    public async Task ClaudeAi_FullFlow_CreatesTheHostsConnection_AndItsAccessTokenCallsAsThatConnection()
    {
        var c = await ConnectAsync(app, app.Host, "claude-ai", ClaudeAiCallback);

        Assert.Equal(OAuthApp.PublicUrl + "/", c.Iss); // RFC 9207
        Assert.Equal("Bearer", c.Tokens["token_type"]!.GetValue<string>(), ignoreCase: true);
        Assert.InRange(c.Tokens["expires_in"]!.GetValue<int>(), 3000, 3600);

        var connection = app.Book.Find(c.ConnectionId)!;
        Assert.Equal("claude-ai", connection.ClientId);
        Assert.Equal(McpSecretOwnerCheck.OwnerId, connection.OwnerId);
        Assert.Equal("claude", connection.Agent);

        var who = await Json(await app.Host.McpAsync(c.AccessToken));
        Assert.Equal(c.ConnectionId.ToString(), who["connection"]!.GetValue<string>());
        Assert.Equal("claude-ai", who["client"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheOwnersChoices_ReachTheHost()
    {
        var c = await ConnectAsync(app, app.Host, "claude-ai", ClaudeAiCallback, new() { ["agent"] = "chatgpt" });
        Assert.Equal("chatgpt", (await Json(await app.Host.McpAsync(c.AccessToken)))["agent"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConsentPage_ShowsNothingOfTheHost_UntilTheOwnerIsProven()
    {
        using var http = app.Host.Browser();
        var page = await http.GetAsync(AuthorizeUrl("claude-ai", ClaudeAiCallback, Pkce.New().Challenge, "s1"));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("claude.ai", html, StringComparison.Ordinal);
        Assert.Contains("name=\"password\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"agent\"", html, StringComparison.Ordinal);

        var unlocked = await Post(http, Hidden(html), new() { ["password"] = app.OwnerSecret, ["consent"] = "continue" });
        var unlockedHtml = await unlocked.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.Contains("name=\"agent\"", unlockedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(app.OwnerSecret, unlockedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consent_WithoutTheOwnerCheck_WithAForgedUnlock_OrByGet_GrantsNothing()
    {
        var before = app.Book.All.Count;
        using var http = app.Host.Browser();
        var url = AuthorizeUrl("claude-ai", ClaudeAiCallback, Pkce.New().Challenge, "s2");
        var hidden = Hidden(await (await http.GetAsync(url)).Content.ReadAsStringAsync());

        foreach (var extra in new Dictionary<string, string>[]
                 {
                     new() { ["consent"] = "allow", ["agent"] = "claude", ["label"] = "x" },
                     new() { ["consent"] = "allow", ["agent"] = "claude", ["label"] = "x", ["password"] = "not-the-owner-secret-but-long-enough-000" },
                     new() { ["consent"] = "allow", ["agent"] = "claude", ["label"] = "x", ["unlock"] = "9999999999.b3duZXI.forged" },
                 })
        {
            var r = await Post(http, hidden, extra);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Null(r.Headers.Location);
        }

        // consent=allow in the query string, even with the owner secret beside it, only shows the page.
        var get = await http.GetAsync(url + "&consent=allow&agent=claude&label=x&password=" + Uri.EscapeDataString(app.OwnerSecret));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Null(get.Headers.Location);

        // The owner secret posted straight with allow proves the owner, but allow still needs the page's own unlock.
        var direct = await Post(http, hidden, new() { ["consent"] = "allow", ["agent"] = "claude", ["label"] = "x", ["password"] = app.OwnerSecret });
        Assert.Equal(HttpStatusCode.BadRequest, direct.StatusCode);
        Assert.Null(direct.Headers.Location);

        Assert.Equal(before, app.Book.All.Count);
    }

    [Fact]
    public async Task AnUnlock_IsBoundToItsAuthorizeRequest()
    {
        using var http = app.Host.Browser();
        var first = Hidden(await (await http.GetAsync(AuthorizeUrl("claude-ai", ClaudeAiCallback, Pkce.New().Challenge, "a"))).Content.ReadAsStringAsync());
        var unlockedHtml = await (await Post(http, first, new() { ["password"] = app.OwnerSecret, ["consent"] = "continue" })).Content.ReadAsStringAsync();
        var unlock = Hidden(unlockedHtml).Single(kv => kv.Key == "unlock").Value;

        // The same unlock on another request (another PKCE challenge, so another code): refused.
        var other = Hidden(await (await http.GetAsync(AuthorizeUrl("claude-ai", ClaudeAiCallback, Pkce.New().Challenge, "a"))).Content.ReadAsStringAsync());
        var r = await Post(http, other, new() { ["consent"] = "allow", ["unlock"] = unlock, ["agent"] = "claude", ["label"] = NewLabel() });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);
    }

    [Fact]
    public async Task Deny_SendsAccessDeniedBackToTheClient_AndCreatesNoConnection()
    {
        var before = app.Book.All.Count;
        using var http = app.Host.Browser();
        var hidden = Hidden(await (await http.GetAsync(AuthorizeUrl("claude-ai", ClaudeAiCallback, Pkce.New().Challenge, "s3"))).Content.ReadAsStringAsync());
        var r = await Post(http, hidden, new() { ["consent"] = "deny" });
        var location = Assert.IsType<Uri>(r.Headers.Location);
        Assert.StartsWith(ClaudeAiCallback, location.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("access_denied", QueryHelpers.ParseQuery(location.Query)["error"].ToString());
        Assert.Equal(before, app.Book.All.Count);
    }

    [Fact]
    public async Task TheHostRefusingTheConnection_ShowsItsMessage_AndIssuesNoCode()
    {
        var before = app.Book.All.Count;
        using var http = app.Host.Browser();
        var hidden = Hidden(await (await http.GetAsync(AuthorizeUrl("claude-ai", ClaudeAiCallback, Pkce.New().Challenge, "s9"))).Content.ReadAsStringAsync());
        var unlockedHtml = await (await Post(http, hidden, new() { ["password"] = app.OwnerSecret, ["consent"] = "continue" })).Content.ReadAsStringAsync();

        var r = await Post(http, Hidden(unlockedHtml), new() { ["consent"] = "allow", ["agent"] = "nobody", ["label"] = NewLabel() });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("No agent &#39;nobody&#39;.", html, StringComparison.Ordinal);
        Assert.Contains("name=\"agent\"", html, StringComparison.Ordinal); // the choices again, to correct
        Assert.Equal(before, app.Book.All.Count);

        // The corrected form goes through.
        var label = NewLabel();
        var approved = await AllowAsync(http, html, ClaudeAiCallback, "s9", new() { ["agent"] = "claude", ["label"] = label });
        Assert.NotEmpty(approved.Code);
        Assert.NotNull(app.Book.FindByLabel(label));
    }

    [Fact]
    public async Task Authorize_RefusesAnotherResource_AnUnlistedRedirect_AndPlainPkce()
    {
        using var http = app.Host.Browser();
        var pkce = Pkce.New();

        var wrongResource = await http.GetAsync(AuthorizeUrl("claude-ai", ClaudeAiCallback, pkce.Challenge, "s4", resource: "https://evil.test/mcp"));
        Assert.Equal("invalid_target", QueryHelpers.ParseQuery(wrongResource.Headers.Location!.Query)["error"].ToString());

        var wrongRedirect = await http.GetAsync(AuthorizeUrl("claude-ai", "https://evil.test/callback", pkce.Challenge, "s5"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongRedirect.StatusCode);
        Assert.Null(wrongRedirect.Headers.Location);

        var plain = await http.GetAsync(AuthorizeUrl("claude-ai", ClaudeAiCallback, pkce.Verifier, "s6", method: "plain"));
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.DoesNotContain("name=\"password\"", await plain.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithASessionOwnerCheck_TheOwnerSignsInFirst_AndAllowStillNeedsThePagesUnlock()
    {
        await using var host = await app.StartHostAsync(OAuthApp.PublicUrl, new CookieOwner());
        var pkce = Pkce.New();
        var url = AuthorizeUrl("claude-ai", ClaudeAiCallback, pkce.Challenge, "c1");

        // Not signed in: off to sign in, with a way back to this very request.
        using (var anonymous = host.Browser())
        {
            var r = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            var signIn = new Uri(new Uri(OAuthApp.PublicUrl), r.Headers.Location!);
            Assert.Equal("/signin", signIn.AbsolutePath);
            var back = QueryHelpers.ParseQuery(signIn.Query)["return"].ToString();
            Assert.StartsWith(OAuthApp.PublicUrl + "/oauth/authorize?", back, StringComparison.Ordinal);
            Assert.Equal(pkce.Challenge, QueryHelpers.ParseQuery(new Uri(back).Query)["code_challenge"].ToString());
        }

        using var alice = host.Browser("owner_session=alice");
        var page = await alice.GetAsync(url);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("name=\"agent\"", html, StringComparison.Ordinal); // signed in: straight to the choices
        Assert.DoesNotContain("name=\"password\"", html, StringComparison.Ordinal);

        // A cross-site post rides on the cookie but cannot know the unlock.
        var before = app.Book.All.Count;
        var forged = await Post(alice, Hidden(html).Where(kv => kv.Key != "unlock").ToList(),
            new() { ["consent"] = "allow", ["agent"] = "claude", ["label"] = NewLabel() });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Null(forged.Headers.Location);

        // Another owner's session cannot use alice's unlock.
        using var mallory = host.Browser("owner_session=mallory");
        var swapped = await Post(mallory, Hidden(html), new() { ["consent"] = "allow", ["agent"] = "claude", ["label"] = NewLabel() });
        Assert.Equal(HttpStatusCode.BadRequest, swapped.StatusCode);
        Assert.Equal(before, app.Book.All.Count);

        var label = NewLabel();
        await AllowAsync(alice, html, ClaudeAiCallback, "c1", new() { ["label"] = label });
        Assert.Equal("alice", app.Book.FindByLabel(label)!.OwnerId);
    }
}
