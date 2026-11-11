using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using static AgentKit.McpOAuth.Tests.OAuthDriver;

namespace AgentKit.McpOAuth.Tests;

/// <summary>
/// Relay's security review 2026-11-11: every authorize request fetched its metadata document, found or not, and nothing
/// limited <c>/oauth/authorize</c>. Each test runs its own host (its own cache and clock) on the shared database.
/// </summary>
public sealed class MetadataCacheTests(OAuthApp app)
{
    private static string NewDocumentUrl() => "https://claude.ai/oauth/cache-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private void Publish(string clientId) =>
        app.MetadataDocuments[clientId] = $$"""{"client_id":"{{clientId}}","client_name":"Cached","redirect_uris":["http://localhost/callback"]}""";

    private static Task<HttpResponseMessage> Authorize(HttpClient http, string clientId) =>
        http.GetAsync(AuthorizeUrl(clientId, "http://localhost:4000/callback", Pkce.New().Challenge, "c"));

    [Fact]
    public async Task AMetadataDocument_IsFetchedOncePerLifetime()
    {
        var clock = new ManualClock();
        await using var host = await app.StartHostAsync(OAuthApp.PublicUrl,
            options: o => o.MetadataDocumentCacheLifetime = TimeSpan.FromMinutes(5), clock: clock);
        using var http = host.Browser();
        var clientId = NewDocumentUrl();
        Publish(clientId);

        Assert.Equal(HttpStatusCode.OK, (await Authorize(http, clientId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Authorize(http, clientId)).StatusCode);
        Assert.Equal(1, app.FetchCount(clientId));

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.OK, (await Authorize(http, clientId)).StatusCode);
        Assert.Equal(2, app.FetchCount(clientId));
    }

    [Fact]
    public async Task AMissingMetadataDocument_IsNotFetchedAgain_UntilItsFailureLifetimeEnds()
    {
        var clock = new ManualClock();
        await using var host = await app.StartHostAsync(OAuthApp.PublicUrl,
            options: o => o.MetadataDocumentFailureCacheLifetime = TimeSpan.FromMinutes(1), clock: clock);
        using var http = host.Browser();
        var clientId = NewDocumentUrl();

        Assert.Equal(HttpStatusCode.BadRequest, (await Authorize(http, clientId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Authorize(http, clientId)).StatusCode);
        Assert.Equal(1, app.FetchCount(clientId));

        // Published meanwhile: picked up once the refusal is forgotten.
        Publish(clientId);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await Authorize(http, clientId)).StatusCode);
        Assert.Equal(2, app.FetchCount(clientId));
    }

    [Fact]
    public async Task TheMetadataDocumentCache_IsBounded_TheOldestEntryLeavesFirst()
    {
        await using var host = await app.StartHostAsync(OAuthApp.PublicUrl, options: o => o.MetadataDocumentCacheSize = 2);
        using var http = host.Browser();
        var (a, b, c) = (NewDocumentUrl(), NewDocumentUrl(), NewDocumentUrl());
        foreach (var id in new[] { a, b, c }) Publish(id);

        await Authorize(http, a);
        await Authorize(http, b);
        await Authorize(http, c);   // full: a leaves
        await Authorize(http, c);
        await Authorize(http, a);

        Assert.Equal(2, app.FetchCount(a));
        Assert.Equal(1, app.FetchCount(c));
    }

    [Fact]
    public async Task TheAuthorizeRateLimit_RefusesBeforeAnyFetch_OnGetAndPost()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("all",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 2, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
        await using var host = await app.StartHostAsync(OAuthApp.PublicUrl, options: o => o.AuthorizeRateLimiter = limiter);
        using var http = host.Browser();
        var (first, limited) = (NewDocumentUrl(), NewDocumentUrl());
        Publish(first);
        Publish(limited);

        Assert.Equal(HttpStatusCode.OK, (await Authorize(http, first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Authorize(http, first)).StatusCode);

        var refused = await Authorize(http, limited);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Null(refused.Headers.Location);
        Assert.Equal(0, app.FetchCount(limited));

        var post = await http.PostAsync("/oauth/authorize", Form(new() { ["client_id"] = "claude-ai", ["consent"] = "continue" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, post.StatusCode);

        // Another host without a limiter is not affected: the limit is the host's own hook.
        using var other = app.Host.Browser();
        Assert.Equal(HttpStatusCode.OK, (await Authorize(other, limited)).StatusCode);
    }
}
