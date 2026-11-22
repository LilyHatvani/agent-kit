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

    private ISocialChannel Channel() =>
        _host.Channels.Mastodon(new MastodonAccount(new Uri("https://mastodon.example/"), Token) { MediaPollInterval = TimeSpan.FromMilliseconds(1) });

    /// <summary>An instance that honours the Idempotency-Key the way Mastodon does: the same key returns the same status.</summary>
    private HttpResponseMessage Mastodon(SeenRequest r)
    {
        if (r.Header("Authorization") != "Bearer " + Token)
            return FakeNetwork.Json(HttpStatusCode.Unauthorized, new { error = "The access token is invalid" });
        var path = r.Uri.AbsolutePath;
        if (path == "/api/v2/media" && r.Method == HttpMethod.Post)
        {
            // Each upload takes the next plan: "ok" (200, processed at once), "slow" (202, then 206 once, then 200),
            // "broken" (202, then 422 on the check), "refused" (422 at upload).
            var plan = MediaPlan.Count > 0 ? MediaPlan.Dequeue() : "ok";
            if (plan == "refused") return FakeNetwork.Json(HttpStatusCode.UnprocessableEntity, new { error = "Validation failed: File content type is invalid" });
            var mediaId = "media-" + (++_media).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _mediaState[mediaId] = plan;
            return FakeNetwork.Json(plan == "ok" ? HttpStatusCode.OK : HttpStatusCode.Accepted, new { id = mediaId, type = "image" });
        }
        if (path.StartsWith("/api/v1/media/", StringComparison.Ordinal))
        {
            var mediaId = path["/api/v1/media/".Length..];
            if (r.Method == HttpMethod.Delete)
            {
                Deleted.Add(mediaId);
                return FakeNetwork.Json(HttpStatusCode.OK, new { id = mediaId });
            }
            switch (_mediaState.GetValueOrDefault(mediaId))
            {
                case "slow":
                    _mediaState[mediaId] = "ok";
                    return FakeNetwork.Json(HttpStatusCode.PartialContent, new { id = mediaId, url = (string?)null });
                case "broken":
                    return FakeNetwork.Json(HttpStatusCode.UnprocessableEntity, new { error = "Processing failed" });
                default:
                    return FakeNetwork.Json(HttpStatusCode.OK, new { id = mediaId, url = $"https://files.example/{mediaId}.png" });
            }
        }
        if (path != "/api/v1/statuses")
            return FakeNetwork.Json(HttpStatusCode.NotFound, new { error = "Record not found" });
        var media = r.Form().Where(f => f.Key == "media_ids[]").Select(f => f.Value).ToList();
        if (media.Any(m => _mediaState.GetValueOrDefault(m) != "ok"))
            return FakeNetwork.Json(HttpStatusCode.UnprocessableEntity, new { error = "Cannot attach files that have not finished processing" });
        var key = r.Header("Idempotency-Key")!;
        if (!_byKey.TryGetValue(key, out var id))
        {
            _byKey[key] = id = (++_statuses).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _statusMedia[id] = media;
        }
        return FakeNetwork.Json(HttpStatusCode.OK, new
        {
            id,
            url = $"https://mastodon.example/@writer/{id}",
            uri = $"https://mastodon.example/users/writer/statuses/{id}",
            media_attachments = _statusMedia[id].Select(m => new { id = m }).ToArray(),
        });
    }

    private readonly Dictionary<string, string> _mediaState = [];
    private readonly Dictionary<string, List<string>> _statusMedia = [];
    private int _media;

    private Queue<string> MediaPlan { get; } = new();

    private List<string> Deleted { get; } = [];

    private static SocialImage Picture(int n) => new() { Bytes = Pictures.Png(100 + n), ContentType = "image/png", Alt = $"Picture {n} of the map" };

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
    public async Task Pictures_upload_as_media_wait_for_processing_and_attach_in_order()
    {
        MediaPlan.Enqueue("ok");
        MediaPlan.Enqueue("slow");
        var result = await Channel().PostAsync(Post() with { Images = [Picture(1), Picture(2)] });

        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Equal(
            ["POST /api/v2/media", "POST /api/v2/media", "GET /api/v1/media/media-2", "GET /api/v1/media/media-2", "POST /api/v1/statuses"],
            _host.Network.Seen.Select(s => $"{s.Method} {s.Uri.AbsolutePath}"));
        var upload = _host.Network.Seen[0];
        Assert.Equal("multipart/form-data", upload.ContentType);
        Assert.Contains("name=file; filename=picture.png", upload.Text, StringComparison.Ordinal);
        Assert.Contains("name=description", upload.Text, StringComparison.Ordinal);
        Assert.Contains("Picture 1 of the map", upload.Text, StringComparison.Ordinal);
        var form = _host.Network.Seen[^1].Form();
        Assert.Equal(["media-1", "media-2"], form.Where(f => f.Key == "media_ids[]").Select(f => f.Value));
        Assert.Contains("https://blog.example.com/posts/lorebook", form.Single(f => f.Key == "status").Value, StringComparison.Ordinal);
        Assert.Empty(Deleted);
    }

    [Fact]
    public async Task A_picture_refused_halfway_posts_nothing_and_deletes_the_ones_already_uploaded()
    {
        MediaPlan.Enqueue("ok");
        MediaPlan.Enqueue("ok");
        MediaPlan.Enqueue("refused");
        var result = await _host.Publisher.PostAsync(Channel(), Post() with { Images = [Picture(1), Picture(2), Picture(3)] });

        Assert.Equal(SocialFailureKind.Rejected, result.Failure!.Kind);
        Assert.False(result.Failure.MayHavePosted);
        Assert.StartsWith("Picture 3 of 3 didn't upload", result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was posted; the 2 already uploaded were deleted.", result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(["media-1", "media-2"], Deleted);
        Assert.DoesNotContain(_host.Network.Seen, s => s.Uri.AbsolutePath == "/api/v1/statuses");
        Assert.Equal(SocialAttemptState.Failed, _host.Ledger.Find("post-1")!.State);
    }

    [Fact]
    public async Task A_picture_whose_processing_fails_posts_nothing_and_cleans_up_including_itself()
    {
        MediaPlan.Enqueue("ok");
        MediaPlan.Enqueue("broken");
        var result = await Channel().PostAsync(Post() with { Images = [Picture(1), Picture(2)] });
        Assert.Equal(SocialFailureKind.Rejected, result.Failure!.Kind);
        Assert.Contains("couldn't process it", result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(["media-1", "media-2"], Deleted);
        Assert.DoesNotContain(_host.Network.Seen, s => s.Uri.AbsolutePath == "/api/v1/statuses");
    }

    [Fact]
    public async Task A_resend_under_the_same_key_keeps_the_first_statuses_pictures_and_deletes_its_own()
    {
        // The first status lands but its answer is lost.
        var inner = _host.Network.Respond;
        var lost = false;
        _host.Network.Respond = r =>
        {
            var answer = inner(r);
            if (r.Uri.AbsolutePath == "/api/v1/statuses" && !lost)
            {
                lost = true;
                answer.Dispose();
                throw new TaskCanceledException("lost");
            }
            return answer;
        };
        await _host.Publisher.PostAsync(Channel(), Post() with { Images = [Picture(1)] });
        var second = await _host.Publisher.PostAsync(Channel(), Post() with { Images = [Picture(1)] });

        Assert.True(second.Posted);
        Assert.Equal(1, _statuses);
        Assert.Equal(["media-2"], Deleted);   // the resend's upload; media-1 is the status's
    }

    [Fact]
    public void Picture_limits_are_the_instances_and_refused_in_compose()
    {
        var channel = Channel();
        Assert.True(channel.Compose(Post() with { Images = [Picture(1), Picture(2), Picture(3), Picture(4)] }).Fits);
        var five = channel.Compose(Post() with { Images = [Picture(1), Picture(2), Picture(3), Picture(4), Picture(5)] });
        Assert.Contains("at most 4 pictures", Assert.Single(five.Problems), StringComparison.Ordinal);

        var gif = new SocialImage { Bytes = Pictures.AnimatedGif(), ContentType = "image/gif", Alt = "A spinning map" };
        Assert.True(channel.Compose(Post() with { Images = [gif] }).Fits);
        // An animated GIF becomes a gifv, which Mastodon lets sit beside pictures (its images_and_video check is audio and video only).
        Assert.True(channel.Compose(Post() with { Images = [gif, Picture(1)] }).Fits);

        var noAlt = channel.Compose(Post() with { Images = [Picture(1) with { Alt = " " }] });
        Assert.Contains("no alt text", Assert.Single(noAlt.Problems), StringComparison.Ordinal);

        var small = _host.Channels.Mastodon(new MastodonAccount(new Uri("https://mastodon.example"), Token) { ImageSizeLimit = 100, MaxMediaAttachments = 2 });
        Assert.Contains("bytes", Assert.Single(small.Compose(Post() with { Images = [Picture(1)] }).Problems), StringComparison.Ordinal);
        Assert.True(channel.Compose(new SocialPost { Text = "", Images = [Picture(1)], IdempotencyKey = "k" }).Fits);
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
