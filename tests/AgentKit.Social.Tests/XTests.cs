using System.Net;

namespace AgentKit.Social.Tests;

/// <summary>
/// X as its docs describe it: POST https://api.x.com/2/tweets with { "text" } and a user token, 201 with data.id
/// (https://docs.x.com/x-api/posts/create-post); problem-details errors ({ title, detail, type }); weighted counting
/// (https://docs.x.com/resources/fundamentals/counting-characters, twitter-text config v3); the token refresh
/// (https://docs.x.com/resources/fundamentals/authentication/oauth-2-0/user-access-token).
/// </summary>
public sealed class XTests : IDisposable
{
    private const string AccessToken = "x-user-access-token-0123456789";
    private readonly TestHost _host = new();

    public XTests()
    {
        _host.Network.Respond = r => r.Header("Authorization") == "Bearer " + AccessToken
            ? FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "1860000000000000001", text = "…" } })
            : FakeNetwork.Json(HttpStatusCode.Unauthorized, new { title = "Unauthorized", type = "about:blank", status = 401, detail = "Unauthorized" });
    }

    public void Dispose() => _host.Dispose();

    private ISocialChannel Channel() => _host.Channels.X(new XAccount(AccessToken));

    private static SocialPost Post(string key = "post-1") => new()
    {
        Text = "New on the blog: a lorebook in twelve entries.",
        Link = new Uri("https://blog.example.com/posts/lorebook"),
        Tags = ["worldbuilding"],
        IdempotencyKey = key,
    };

    [Fact]
    public async Task A_post_is_json_text_with_the_user_token()
    {
        var result = await Channel().PostAsync(Post());

        Assert.True(result.Posted, result.Failure?.Message);
        var r = Assert.Single(_host.Network.Seen);
        Assert.Equal(new Uri("https://api.x.com/2/tweets"), r.Uri);
        Assert.Equal("application/json", r.ContentType);
        Assert.Equal("Bearer " + AccessToken, r.Header("Authorization"));
        Assert.Equal("New on the blog: a lorebook in twelve entries.\n\nhttps://blog.example.com/posts/lorebook\n\n#worldbuilding",
            r.Json.GetProperty("text").GetString());
        Assert.Equal("1860000000000000001", result.PostId);
        Assert.Equal(new Uri("https://x.com/i/web/status/1860000000000000001"), result.PostUrl);
    }

    [Theory]
    [InlineData("hello", 5)]
    [InlineData("café", 4)]
    [InlineData("café", 4)]                               // NFC first: decomposed é is one character
    [InlineData("日本語", 6)]                                     // CJK weighs 2
    [InlineData("😀", 2)]
    [InlineData("👨‍👩‍👧‍👦", 2)]                                     // an emoji sequence weighs 2 whatever its length
    [InlineData("“quoted” — dash", 15)]                         // U+2010–U+201F weigh 1
    [InlineData("see https://example.com/a/very/long/path/that/goes/on/and/on", 27)]   // 4 + a URL at 23
    public void Weighted_length_follows_twitter_text_v3(string text, int weight) =>
        Assert.Equal(weight, Channel().Compose(new SocialPost { Text = text, IdempotencyKey = "k" }).Length);

    [Fact]
    public void Past_280_weighted_is_refused_and_premium_can_raise_it()
    {
        Assert.True(Channel().Compose(new SocialPost { Text = new string('a', 280), IdempotencyKey = "k" }).Fits);
        Assert.False(Channel().Compose(new SocialPost { Text = new string('日', 141), IdempotencyKey = "k" }).Fits);
        Assert.True(_host.Channels.X(new XAccount(AccessToken) { MaxWeightedLength = 25_000 })
            .Compose(new SocialPost { Text = new string('a', 2000), IdempotencyKey = "k" }).Fits);
    }

    [Fact]
    public async Task Failures_map_and_a_duplicate_is_a_rejection_with_xs_words()
    {
        var expired = await _host.Channels.X(new XAccount("expired-token-000")).PostAsync(Post());
        Assert.Equal(SocialFailureKind.Auth, expired.Failure!.Kind);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.Forbidden,
            new { title = "Forbidden", type = "about:blank", status = 403, detail = "You are not allowed to create a Tweet with duplicate content." });
        var duplicate = await Channel().PostAsync(Post());
        Assert.Equal(SocialFailureKind.Rejected, duplicate.Failure!.Kind);
        Assert.Contains("duplicate content", duplicate.Failure.Message, StringComparison.Ordinal);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.TooManyRequests, new { title = "Too Many Requests", status = 429, detail = "Too Many Requests" },
            ("x-rate-limit-reset", TestHost.Start.AddMinutes(15).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var limited = await Channel().PostAsync(Post());
        Assert.Equal(SocialFailureKind.RateLimited, limited.Failure!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(15), limited.Failure.RetryAfter);
    }

    [Fact]
    public async Task A_retry_after_a_lost_answer_is_unconfirmed()
    {
        _host.Network.Fail = r => _host.Network.Seen.Count == 1 ? new TaskCanceledException("timeout") : null;
        var first = await _host.Publisher.PostAsync(Channel(), Post());
        Assert.True(first.Failure!.MayHavePosted);

        var again = await _host.Publisher.PostAsync(Channel(), Post());
        Assert.Equal(SocialFailureKind.Unconfirmed, again.Failure!.Kind);
        Assert.Single(_host.Network.Seen);
    }

    [Fact]
    public async Task A_token_refresh_trades_the_refresh_token_and_returns_the_rotated_pair()
    {
        _host.Network.Respond = r => FakeNetwork.Json(HttpStatusCode.OK,
            new { token_type = "bearer", expires_in = 7200, access_token = "new-access", refresh_token = "new-refresh", scope = "tweet.write tweet.read users.read offline.access" });
        var refreshed = await _host.Channels.RefreshXTokenAsync(new XClient("client-id-1", "client-secret-1"), "old-refresh-token");

        Assert.Null(refreshed.Failure);
        Assert.Equal("new-access", refreshed.Tokens!.AccessToken);
        Assert.Equal("new-refresh", refreshed.Tokens.RefreshToken);
        Assert.Equal(TestHost.Start.AddHours(2), refreshed.Tokens.ExpiresAt);
        var r = Assert.Single(_host.Network.Seen);
        Assert.Equal(new Uri("https://api.x.com/2/oauth2/token"), r.Uri);
        Assert.Equal([new("grant_type", "refresh_token"), new("refresh_token", "old-refresh-token"), new("client_id", "client-id-1")], r.Form());
        Assert.Equal("Basic " + Convert.ToBase64String("client-id-1:client-secret-1"u8.ToArray()), r.Header("Authorization"));
        Assert.DoesNotContain("new-access", refreshed.Tokens.ToString(), StringComparison.Ordinal);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "Value passed for the token was invalid." });
        var spent = await _host.Channels.RefreshXTokenAsync(new XClient("client-id-1"), "old-refresh-token");
        Assert.Equal(SocialFailureKind.Auth, spent.Failure!.Kind);
        Assert.Null(_host.Network.Seen[^1].Header("Authorization"));
    }
}
