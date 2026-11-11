using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using static AgentKit.McpOAuth.Tests.OAuthDriver;

namespace AgentKit.McpOAuth.Tests;

/// <summary>
/// Relay's security review 2026-11-11: a loopback redirect counts only when its host is written literally, and a metadata
/// document client id only when written canonically.
/// </summary>
public sealed class ClientHardeningTests(OAuthApp app)
{
    /// <summary>
    /// .NET reads the host <c>loopback</c> as <c>localhost</c>, but a browser looks the name up, so a code sent there can
    /// leave the machine. Userinfo, a trailing dot, padding and shorthand IPs are other spellings a browser and .NET may
    /// read differently. Only <c>127.0.0.1</c>, <c>[::1]</c> and <c>localhost</c> as written count (RFC 8252 § 8.3).
    /// </summary>
    [Theory]
    [InlineData("http://loopback:8080/callback")]
    [InlineData("http://LOOPBACK:8080/callback")]
    [InlineData("http://user@localhost:8080/callback")]
    [InlineData("http://user@127.0.0.1:8080/callback")]
    [InlineData("http://localhost.:8080/callback")]
    [InlineData("http://127.1:8080/callback")]
    [InlineData(" http://localhost:8080/callback")]
    [InlineData("\thttp://localhost:8080/callback")]
    [InlineData("http://localhost:8080/callback ")]
    [InlineData("http:/loopback:8080/callback")]
    public async Task Authorize_ALoopbackRedirectNotWrittenLiterally_IsRefused(string redirect)
    {
        using var http = app.Host.Browser();
        var r = await http.GetAsync(AuthorizeUrl("claude-code", redirect, Pkce.New().Challenge, "lb"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);
        Assert.DoesNotContain("name=\"password\"", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>The same refusal however the request is spelled: the path's case and trailing slash, the scheme's case, a repeated parameter.</summary>
    [Theory]
    [InlineData("/oauth/authorize/", "http://loopback:8080/callback", false)]
    [InlineData("/OAUTH/Authorize", "http://loopback:8080/callback", false)]
    [InlineData("/oauth/authorize", "HTTP://loopback:8080/callback", false)]
    [InlineData("/oauth/authorize", "http://localhost:8080/callback", true)]
    public async Task Authorize_ALoopbackLookalike_IsRefused_HoweverTheRequestIsSpelled(string path, string redirect, bool repeatWithLookalike)
    {
        var url = AuthorizeUrl("claude-code", redirect, Pkce.New().Challenge, "lb-spell").Replace("/oauth/authorize", path, StringComparison.Ordinal);
        if (repeatWithLookalike) url += "&redirect_uri=" + Uri.EscapeDataString("http://loopback:8080/callback");
        using var http = app.Host.Browser();
        var r = await http.GetAsync(url);

        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.True((int)r.StatusCode < 500, $"{r.StatusCode}");
        Assert.Null(r.Headers.Location);
        Assert.DoesNotContain("name=\"password\"", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIpv6LoopbackRedirect_MatchesOnlyAsWritten()
    {
        await using var host = await app.StartHostAsync(OAuthApp.PublicUrl,
            options: o => o.Clients.Add(new McpOAuthClient("review-ipv6", "IPv6 app", ["http://[::1]/callback"])));
        using var http = host.Browser();

        var literal = await http.GetAsync(AuthorizeUrl("review-ipv6", "http://[::1]:5555/callback", Pkce.New().Challenge, "v6"));
        Assert.Equal(HttpStatusCode.OK, literal.StatusCode);
        var longForm = await http.GetAsync(AuthorizeUrl("review-ipv6", "http://[0:0:0:0:0:0:0:1]:5555/callback", Pkce.New().Challenge, "v6-long"));
        Assert.Equal(HttpStatusCode.BadRequest, longForm.StatusCode);
        Assert.Null(longForm.Headers.Location);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/callback")]
    [InlineData("http://LOCALHOST:8080/callback")]
    public async Task Authorize_ALiteralLoopbackRedirect_StillMatchesOnAnyPort(string redirect)
    {
        using var http = app.Host.Browser();
        var r = await http.GetAsync(AuthorizeUrl("claude-code", redirect, Pkce.New().Challenge, "lb-ok"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Authorize_ALoopbackLookalike_IsRefused_OnTheConsentPostToo()
    {
        var query = QueryHelpers.ParseQuery(new Uri(OAuthApp.PublicUrl + AuthorizeUrl("claude-code", "http://loopback:8080/callback",
            Pkce.New().Challenge, "lb-post")).Query);
        var form = query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        form["consent"] = "continue";
        form["password"] = app.OwnerSecret;
        using var http = app.Host.Browser();
        var r = await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);
        Assert.DoesNotContain("name=\"unlock\"", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMetadataDocument_NamingALoopbackLookalikeRedirect_IsRefused()
    {
        const string clientId = "https://claude.ai/oauth/review-loopback-lookalike";
        app.MetadataDocuments[clientId] = $$"""{"client_id":"{{clientId}}","redirect_uris":["http://loopback/callback"]}""";
        using var http = app.Host.Browser();
        var r = await http.GetAsync(AuthorizeUrl(clientId, "http://loopback:8080/callback", Pkce.New().Challenge, "lb-doc"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);
    }

    /// <summary>
    /// An alias of a metadata document URL (dot segments, case, the default port) fetches the same document, but each would
    /// register a client of its own under the alias.
    /// </summary>
    [Theory]
    [InlineData("https://claude.ai/oauth/./review-canonical")]
    [InlineData("https://claude.ai/x/../oauth/review-canonical")]
    [InlineData("https://CLAUDE.AI/oauth/review-canonical")]
    [InlineData("https://claude.ai:443/oauth/review-canonical")]
    public async Task Authorize_AnAliasOfAMetadataDocumentUrl_IsRefused(string alias)
    {
        const string clientId = "https://claude.ai/oauth/review-canonical";
        app.MetadataDocuments[clientId] = $$"""{"client_id":"{{clientId}}","client_name":"Canonical","redirect_uris":["http://localhost/callback"]}""";
        using var http = app.Host.Browser();

        var r = await http.GetAsync(AuthorizeUrl(alias, "http://localhost:3119/callback", Pkce.New().Challenge, "alias"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);

        // The canonical spelling is the client.
        var ok = await http.GetAsync(AuthorizeUrl(clientId, "http://localhost:3119/callback", Pkce.New().Challenge, "canonical"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task AClientStoredUnderAnAlias_ByAnEarlierVersion_IsNotUsed()
    {
        // A row as 0.1.4 stored it after an alias was presented (here seeded by another host on the same database).
        const string alias = "https://claude.ai:443/oauth/review-stale-alias";
        await using (await app.StartHostAsync(OAuthApp.PublicUrl,
                         options: o => o.Clients.Add(new McpOAuthClient(alias, "Stale", ["http://localhost/callback"])))) { }

        using var http = app.Host.Browser();
        var get = await http.GetAsync(AuthorizeUrl(alias, "http://localhost:3120/callback", Pkce.New().Challenge, "stale"));
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
        Assert.Null(get.Headers.Location);

        var query = QueryHelpers.ParseQuery(new Uri(OAuthApp.PublicUrl + AuthorizeUrl(alias, "http://localhost:3120/callback",
            Pkce.New().Challenge, "stale-post")).Query);
        var form = query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        form["consent"] = "continue";
        form["password"] = app.OwnerSecret;
        var post = await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.DoesNotContain("name=\"unlock\"", await post.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
