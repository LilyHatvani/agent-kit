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
    [InlineData("👍🏽", 2)]                                       // a skin-tone sequence weighs 2
    [InlineData("1️⃣", 2)]                             // a keycap weighs 2
    [InlineData("❤︎", 4)]                              // ❤︎ with the text selector: twitter-text counts 4
    [InlineData("a⃣", 3)]                                   // a keycap on a letter: twitter-text counts 3
    [InlineData("\U0001F600‍\U0001F600", 5)]                // a ZWJ pair no table knows: twitter-text counts 5
    [InlineData("👨‍👩‍👧‍👦", 11)]                                    // a ZWJ join is charged per part: never under X's count
    [InlineData("“quoted” — dash", 15)]                         // U+2010–U+201F weigh 1
    [InlineData("see https://example.com/a/very/long/path/that/goes/on/and/on", 27)]   // 4 + a URL at 23
    [InlineData("see https://en.wikipedia.org/wiki/Rust_(programming_language)", 27)]  // the parenthesis is the URL's
    [InlineData("visit example.com now", 33)]                   // a bare domain counts as a link, as twitter-text does
    [InlineData("v2.0", 23)]                                    // a maybe-domain counts as one: never under
    public void Weighted_length_is_never_under_twitter_text_v3(string text, int weight) =>
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

    /// <summary>
    /// The media flow as X's docs give it: POST /2/media/upload (multipart media + media_category), GET /2/media/upload
    /// ?command=STATUS while processing, POST /2/media/metadata for alt text, then media.media_ids on the post.
    /// </summary>
    private HttpResponseMessage XWithMedia(SeenRequest r, Func<int, string?>? uploadState = null, int failUpload = 0)
    {
        if (r.Header("Authorization") != "Bearer " + AccessToken)
            return FakeNetwork.Json(HttpStatusCode.Unauthorized, new { title = "Unauthorized", status = 401 });
        switch (r.Uri.AbsolutePath)
        {
            case "/2/media/upload" when r.Method == HttpMethod.Post:
                var n = ++_uploads;
                if (n == failUpload)
                    return FakeNetwork.Json(HttpStatusCode.BadRequest, new { title = "Invalid Request", detail = "Media type unrecognized.", status = 400 });
                var state = uploadState?.Invoke(n);
                object data = state is null
                    ? new { id = $"9{n}", expires_after_secs = 86400 }
                    : new { id = $"9{n}", expires_after_secs = 86400, processing_info = new { state, check_after_secs = 1 } };
                return FakeNetwork.Json(HttpStatusCode.OK, new { data });
            case "/2/media/upload":
                return FakeNetwork.Json(HttpStatusCode.OK, new { data = new { id = "91", processing_info = new { state = "succeeded", progress_percent = 100 } } });
            case "/2/media/metadata":
                return FakeNetwork.Json(HttpStatusCode.OK, new { data = new { id = r.Json.GetProperty("id").GetString(), associated_metadata = true } });
            case "/2/tweets":
                return FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "1860000000000000002", text = "…" } });
            default:
                return FakeNetwork.Json(HttpStatusCode.NotFound, new { title = "Not Found" });
        }
    }

    private int _uploads;

    private static SocialImage Picture(int n) => new() { Bytes = Pictures.Png(200 + n), ContentType = "image/png", Alt = $"Map {n}" };

    [Fact]
    public async Task Pictures_upload_as_media_with_their_alt_text_and_go_on_the_post_as_media_ids()
    {
        _host.Network.Respond = r => XWithMedia(r);
        var result = await Channel().PostAsync(Post() with { Images = [Picture(1), Picture(2)] });

        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Equal(["POST /2/media/upload", "POST /2/media/metadata", "POST /2/media/upload", "POST /2/media/metadata", "POST /2/tweets"],
            _host.Network.Seen.Select(s => $"{s.Method} {s.Uri.AbsolutePath}"));
        var upload = _host.Network.Seen[0];
        Assert.Equal("multipart/form-data", upload.ContentType);
        Assert.Contains("name=media; filename=picture.png", upload.Text, StringComparison.Ordinal);
        Assert.Contains("tweet_image", upload.Text, StringComparison.Ordinal);
        var alt = _host.Network.Seen[1].Json;
        Assert.Equal("91", alt.GetProperty("id").GetString());
        Assert.Equal("Map 1", alt.GetProperty("metadata").GetProperty("alt_text").GetProperty("text").GetString());
        var tweet = _host.Network.Seen[^1].Json;
        Assert.Equal(["91", "92"], tweet.GetProperty("media").GetProperty("media_ids").EnumerateArray().Select(m => m.GetString()));
        Assert.Contains("https://blog.example.com/posts/lorebook", tweet.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_animated_gif_goes_as_tweet_gif_and_waits_for_processing()
    {
        _host.Network.Respond = r => XWithMedia(r, uploadState: _ => "pending");
        var gif = new SocialImage { Bytes = Pictures.AnimatedGif(), ContentType = "image/gif", Alt = "A spinning map" };
        var account = new XAccount(AccessToken) { MediaPollInterval = TimeSpan.FromMilliseconds(1) };
        var result = await _host.Channels.X(account).PostAsync(Post() with { Images = [gif] });

        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Contains("tweet_gif", _host.Network.Seen[0].Text, StringComparison.Ordinal);
        var status = _host.Network.Seen[1];
        Assert.Equal(HttpMethod.Get, status.Method);
        Assert.Equal("?command=STATUS&media_id=91", status.Uri.Query);
    }

    [Fact]
    public async Task A_picture_refused_halfway_posts_nothing_and_says_the_uploaded_ones_expire()
    {
        _host.Network.Respond = r => XWithMedia(r, failUpload: 2);
        var result = await _host.Publisher.PostAsync(Channel(), Post() with { Images = [Picture(1), Picture(2)] });

        Assert.Equal(SocialFailureKind.Rejected, result.Failure!.Kind);
        Assert.False(result.Failure.MayHavePosted);
        Assert.StartsWith("Picture 2 of 2 didn't upload", result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("Media type unrecognized", result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("expire unused", result.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.Network.Seen, s => s.Uri.AbsolutePath == "/2/tweets");
        Assert.Equal(SocialAttemptState.Failed, _host.Ledger.Find("post-1")!.State);
    }

    [Fact]
    public void Picture_limits_are_xs_and_refused_in_compose()
    {
        var channel = Channel();
        Assert.Contains("at most 4", Assert.Single(channel.Compose(Post() with { Images = [Picture(1), Picture(2), Picture(3), Picture(4), Picture(5)] }).Problems), StringComparison.Ordinal);
        Assert.Contains("5,000,000", Assert.Single(channel.Compose(Post() with { Images = [Picture(1) with { Bytes = Pictures.Png(5_000_001) }] }).Problems), StringComparison.Ordinal);
        var gif = new SocialImage { Bytes = Pictures.AnimatedGif(), ContentType = "image/gif", Alt = "A spinning map" };
        Assert.Contains("goes alone", Assert.Single(channel.Compose(Post() with { Images = [gif, Picture(1)] }).Problems), StringComparison.Ordinal);
        Assert.Contains("1000", Assert.Single(channel.Compose(Post() with { Images = [Picture(1) with { Alt = new string('a', 1001) }] }).Problems), StringComparison.Ordinal);
    }
}
