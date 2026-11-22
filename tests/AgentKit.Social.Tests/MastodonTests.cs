using System.Net;

namespace AgentKit.Social.Tests;

/// <summary>
/// Mastodon as its docs describe it: POST /api/v1/statuses with form fields and an Idempotency-Key kept "for up to 1 hour"
/// (https://docs.joinmastodon.org/methods/statuses/#create), errors as { "error": "…" } (401 "The access token is
/// invalid", 422 "Validation failed: …"), POST /api/v2/media (https://docs.joinmastodon.org/methods/media/#v2), URL
/// counting per configuration.statuses (https://docs.joinmastodon.org/methods/instance/#v2), X-RateLimit-Reset as an
/// ISO 8601 date (https://docs.joinmastodon.org/api/rate-limits/).
/// </summary>
public sealed class MastodonTests : IDisposable
{
    private const string Token = "mastodon-user-token-0123456789";
    private readonly TestHost _host = new();
    private readonly Dictionary<string, string> _byKey = [];
    private int _statuses;

    public MastodonTests()
    {
        _host.Network.Respond = Mastodon;
    }

    public void Dispose() => _host.Dispose();

    private ISocialChannel Channel(bool attach = false) =>
        _host.Channels.Mastodon(new MastodonAccount(new Uri("https://mastodon.example/"), Token) { AttachCardImage = attach });

    /// <summary>An instance that honours the Idempotency-Key the way Mastodon does: the same key returns the same status.</summary>
    private HttpResponseMessage Mastodon(SeenRequest r)
    {
        if (r.Header("Authorization") != "Bearer " + Token)
            return FakeNetwork.Json(HttpStatusCode.Unauthorized, new { error = "The access token is invalid" });
        if (r.Uri.AbsolutePath == "/api/v2/media")
            return FakeNetwork.Json(HttpStatusCode.OK, new { id = "media-1", type = "image" });
        if (r.Uri.AbsolutePath != "/api/v1/statuses")
            return FakeNetwork.Json(HttpStatusCode.NotFound, new { error = "Record not found" });
        var key = r.Header("Idempotency-Key")!;
        if (!_byKey.TryGetValue(key, out var id)) _byKey[key] = id = (++_statuses).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return FakeNetwork.Json(HttpStatusCode.OK, new { id, url = $"https://mastodon.example/@writer/{id}", uri = $"https://mastodon.example/users/writer/statuses/{id}" });
    }

    private static SocialPost Post(string key = "post-1") => new()
    {
        Text = "New on the blog: a lorebook in twelve entries.",
        Link = new Uri("https://blog.example.com/posts/lorebook"),
        Card = new LinkCard { Title = "A lorebook", Image = Pictures.Png(100), ImageAlt = "A map of the world" },
        Tags = ["worldbuilding"],
        Language = "en",
        IdempotencyKey = key,
    };

    [Fact]
    public async Task A_status_goes_as_a_form_with_the_token_and_an_idempotency_key()
    {
        var result = await Channel().PostAsync(Post());

        Assert.True(result.Posted, result.Failure?.Message);
        var r = Assert.Single(_host.Network.Seen);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal(new Uri("https://mastodon.example/api/v1/statuses"), r.Uri);
        Assert.Equal("application/x-www-form-urlencoded", r.ContentType);
        Assert.Equal("Bearer " + Token, r.Header("Authorization"));
        Assert.Matches("^[0-9a-f]{32}$", r.Header("Idempotency-Key"));
        Assert.Equal(
            [
                new("status", "New on the blog: a lorebook in twelve entries.\n\nhttps://blog.example.com/posts/lorebook\n\n#worldbuilding"),
                new("visibility", "public"),
                new("language", "en"),
            ],
            r.Form());
        Assert.Equal("1", result.PostId);
        Assert.Equal(new Uri("https://mastodon.example/@writer/1"), result.PostUrl);
    }

    [Fact]
    public void Every_url_counts_as_the_instances_reserved_length_and_the_limit_is_the_instances()
    {
        var channel = Channel();
        var longLink = new Uri("https://blog.example.com/" + new string('a', 200));
        var c = channel.Compose(new SocialPost { Text = new string('x', 475), Link = longLink, IdempotencyKey = "k" });
        Assert.Equal(475 + 2 + 23, c.Length);
        Assert.True(c.Fits);
        Assert.False(channel.Compose(new SocialPost { Text = new string('x', 476), Link = longLink, IdempotencyKey = "k" }).Fits);

        var big = _host.Channels.Mastodon(new MastodonAccount(new Uri("https://mastodon.example"), Token) { MaxCharacters = 5000 });
        Assert.True(big.Compose(new SocialPost { Text = new string('x', 4000), IdempotencyKey = "k" }).Fits);
    }

    [Fact]
    public async Task A_retry_inside_the_hour_resends_the_same_key_and_the_instance_returns_the_same_status()
    {
        _host.Network.Fail = r => _host.Network.Seen.Count == 1 ? new HttpRequestException("reset", null, HttpStatusCode.OK) : null;
        var first = await _host.Publisher.PostAsync(Channel(), Post());
        Assert.Equal(SocialFailureKind.Transient, first.Failure!.Kind);
        Assert.True(first.Failure.MayHavePosted);

        _host.Clock.Advance(TimeSpan.FromMinutes(30));
        var second = await _host.Publisher.PostAsync(Channel(), Post());
        Assert.True(second.Posted);
        Assert.Equal(2, _host.Network.Seen.Count);
        Assert.Equal(_host.Network.Seen[0].Header("Idempotency-Key"), _host.Network.Seen[1].Header("Idempotency-Key"));
        Assert.Equal(1, _statuses);
    }

    [Fact]
    public async Task Past_the_hour_a_retry_is_unconfirmed_until_a_person_asks_to_resend()
    {
        _host.Network.Fail = r => _host.Network.Seen.Count == 1 ? new TaskCanceledException("timeout") : null;
        await _host.Publisher.PostAsync(Channel(), Post());

        _host.Clock.Advance(TimeSpan.FromMinutes(61));
        var unconfirmed = await _host.Publisher.PostAsync(Channel(), Post());
        Assert.Equal(SocialFailureKind.Unconfirmed, unconfirmed.Failure!.Kind);
        Assert.Single(_host.Network.Seen);

        var resent = await _host.Publisher.PostAsync(Channel(), Post(), resendUnconfirmed: true);
        Assert.True(resent.Posted);
        Assert.Equal(2, _host.Network.Seen.Count);
    }

    [Fact]
    public async Task A_card_picture_is_attached_only_when_asked_with_its_alt_text()
    {
        await Channel().PostAsync(Post("a"));
        Assert.DoesNotContain(_host.Network.Seen, s => s.Uri.AbsolutePath == "/api/v2/media");

        var result = await Channel(attach: true).PostAsync(Post("b"));
        Assert.True(result.Posted);
        var media = _host.Network.Seen.Single(s => s.Uri.AbsolutePath == "/api/v2/media");
        Assert.Equal("multipart/form-data", media.ContentType);
        Assert.Contains("name=file; filename=card.png", media.Text, StringComparison.Ordinal);
        Assert.Contains("A map of the world", media.Text, StringComparison.Ordinal);
        Assert.Contains(new KeyValuePair<string, string>("media_ids[]", "media-1"), _host.Network.Seen[^1].Form());
    }

    [Fact]
    public async Task Failures_map_to_their_category()
    {
        var wrong = await _host.Channels.Mastodon(new MastodonAccount(new Uri("https://mastodon.example"), "not-the-token")).PostAsync(Post());
        Assert.Equal(SocialFailureKind.Auth, wrong.Failure!.Kind);
        Assert.Contains("The access token is invalid", wrong.Failure.Message, StringComparison.Ordinal);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.UnprocessableEntity, new { error = "Validation failed: Text character limit of 500 exceeded" });
        var invalid = await Channel().PostAsync(Post());
        Assert.Equal(SocialFailureKind.Rejected, invalid.Failure!.Kind);
        Assert.Contains("Validation failed", invalid.Failure.Message, StringComparison.Ordinal);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.TooManyRequests, new { error = "Too many requests" },
            ("X-RateLimit-Reset", TestHost.Start.AddMinutes(3).ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        var limited = await Channel().PostAsync(Post());
        Assert.Equal(SocialFailureKind.RateLimited, limited.Failure!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(3), limited.Failure.RetryAfter);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.ServiceUnavailable, "");
        var down = await Channel().PostAsync(Post());
        Assert.Equal(SocialFailureKind.Transient, down.Failure!.Kind);
    }

    [Fact]
    public void An_instance_that_isnt_public_https_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _host.Channels.Mastodon(new MastodonAccount(new Uri("http://mastodon.example"), Token)));
        Assert.Throws<ArgumentException>(() => _host.Channels.Mastodon(new MastodonAccount(new Uri("https://10.0.0.5"), Token)));
        Assert.Throws<ArgumentException>(() => _host.Channels.Mastodon(new MastodonAccount(new Uri("https://localhost"), Token)));
    }
}
