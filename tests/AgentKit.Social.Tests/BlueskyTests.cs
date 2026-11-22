using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentKit.Social.Tests;

/// <summary>
/// Bluesky as its docs describe it:
/// createSession https://docs.bsky.app/docs/api/com-atproto-server-create-session,
/// createRecord https://docs.bsky.app/docs/api/com-atproto-repo-create-record,
/// getRecord https://docs.bsky.app/docs/api/com-atproto-repo-get-record,
/// uploadBlob and the external card https://docs.bsky.app/docs/advanced-guides/posts,
/// facets (UTF-8 byte offsets) https://docs.bsky.app/docs/advanced-guides/post-richtext and the lexicon
/// app.bsky.richtext.facet, text limits in the lexicon app.bsky.feed.post (maxGraphemes 300, maxLength 3000).
/// </summary>
public sealed class BlueskyTests : IDisposable
{
    private const string Did = "did:plc:abc123";
    private const string Password = "abcd-efgh-ijkl-mnop";
    private readonly TestHost _host = new();
    private readonly Dictionary<string, string> _records = [];
    private int _sessions;

    public BlueskyTests()
    {
        _host.Network.Respond = Bluesky;
    }

    public void Dispose() => _host.Dispose();

    private ISocialChannel Channel() => _host.Channels.Bluesky(new BlueskyAccount("writer.bsky.social", Password));

    /// <summary>A PDS: a session for the right app password, blobs, records keyed by rkey.</summary>
    private HttpResponseMessage Bluesky(SeenRequest r)
    {
        var path = r.Uri.AbsolutePath;
        if (path == "/xrpc/com.atproto.server.createSession")
        {
            if (r.Json.GetProperty("password").GetString() != Password)
                return FakeNetwork.Json(HttpStatusCode.Unauthorized, new { error = "AuthenticationRequired", message = "Invalid identifier or password" });
            _sessions++;
            return FakeNetwork.Json(HttpStatusCode.OK, new { accessJwt = $"access-{_sessions}", refreshJwt = "refresh", handle = "writer.bsky.social", did = Did });
        }
        if (r.Header("Authorization") is not { } auth || !auth.StartsWith("Bearer access-", StringComparison.Ordinal))
            return FakeNetwork.Json(HttpStatusCode.Unauthorized, new { error = "AuthenticationRequired", message = "no session" });
        switch (path)
        {
            case "/xrpc/com.atproto.repo.uploadBlob":
                return FakeNetwork.Json(HttpStatusCode.OK, new { blob = new Dictionary<string, object>
                {
                    ["$type"] = "blob",
                    ["ref"] = new Dictionary<string, string> { ["$link"] = "bafkreithumb" },
                    ["mimeType"] = r.ContentType!,
                    ["size"] = r.Body.Length,
                } });
            case "/xrpc/com.atproto.repo.createRecord":
                var rkey = r.Json.GetProperty("rkey").GetString()!;
                if (_records.ContainsKey(rkey))
                    return FakeNetwork.Json(HttpStatusCode.BadRequest, new { error = "InvalidRequest", message = "Record already exists" });
                _records[rkey] = r.Text;
                return FakeNetwork.Json(HttpStatusCode.OK, new { uri = $"at://{Did}/app.bsky.feed.post/{rkey}", cid = "bafyrecord" });
            case "/xrpc/com.atproto.repo.getRecord":
                var q = System.Web.HttpUtility.ParseQueryString(r.Uri.Query);
                return _records.ContainsKey(q["rkey"]!)
                    ? FakeNetwork.Json(HttpStatusCode.OK, new { uri = $"at://{Did}/app.bsky.feed.post/{q["rkey"]}", cid = "bafyrecord", value = new { } })
                    : FakeNetwork.Json(HttpStatusCode.BadRequest, new { error = "RecordNotFound", message = "Could not locate record" });
            default:
                return FakeNetwork.Json(HttpStatusCode.NotFound, new { error = "MethodNotImplemented" });
        }
    }

    private static SocialPost Post(string text = "Café ✨ notes on lorebooks", string key = "post-1") => new()
    {
        Text = text,
        Link = new Uri("https://blog.example.com/posts/how-to-write-a-lorebook-that-remembers"),
        Card = new LinkCard { Title = "How to write a lorebook", Description = "Twelve entries, one world.", Image = Pictures.Png(2048), ImageAlt = "A map" },
        Tags = ["#worldbuilding", "writing tips"],
        Language = "en",
        IdempotencyKey = key,
    };

    [Fact]
    public async Task A_post_is_a_record_with_byte_indexed_facets_and_a_card_whose_thumb_was_uploaded()
    {
        var result = await Channel().PostAsync(Post());

        Assert.True(result.Posted, result.Failure?.Message);
        var seen = _host.Network.Seen;
        Assert.Equal(["/xrpc/com.atproto.server.createSession", "/xrpc/com.atproto.repo.uploadBlob", "/xrpc/com.atproto.repo.createRecord"],
            seen.Select(s => s.Uri.AbsolutePath));
        Assert.All(seen, s => Assert.Equal("bsky.social", s.Uri.Host));
        Assert.Equal("writer.bsky.social", seen[0].Json.GetProperty("identifier").GetString());

        var upload = seen[1];
        Assert.Equal("image/png", upload.ContentType);
        Assert.Equal(2048, upload.Body.Length);
        Assert.Equal("Bearer access-1", upload.Header("Authorization"));

        var create = seen[2].Json;
        Assert.Equal(Did, create.GetProperty("repo").GetString());
        Assert.Equal("app.bsky.feed.post", create.GetProperty("collection").GetString());
        var rkey = create.GetProperty("rkey").GetString()!;
        Assert.Matches("^[2-7a-j][2-7a-z]{12}$", rkey);   // a TID (https://atproto.com/specs/tid)

        var record = create.GetProperty("record");
        Assert.Equal("app.bsky.feed.post", record.GetProperty("$type").GetString());
        var text = record.GetProperty("text").GetString()!;
        Assert.Equal("Café ✨ notes on lorebooks\n\nblog.example.com/posts/how-...\n\n#worldbuilding #writingtips", text);
        Assert.Equal(["en"], record.GetProperty("langs").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("2026-11-22T12:00:00.000Z", record.GetProperty("createdAt").GetString());

        // Facets index UTF-8 bytes: "é" is 2 bytes and "✨" 3, so the link starts at byte 30, not at UTF-16 index 27.
        var facets = record.GetProperty("facets").EnumerateArray().ToList();
        var bytes = Encoding.UTF8.GetBytes(text);
        string Slice(JsonElement f) => Encoding.UTF8.GetString(bytes,
            f.GetProperty("index").GetProperty("byteStart").GetInt32(),
            f.GetProperty("index").GetProperty("byteEnd").GetInt32() - f.GetProperty("index").GetProperty("byteStart").GetInt32());
        var link = Assert.Single(facets, f => f.GetProperty("features")[0].GetProperty("$type").GetString() == "app.bsky.richtext.facet#link");
        Assert.Equal(30, link.GetProperty("index").GetProperty("byteStart").GetInt32());
        Assert.Equal("blog.example.com/posts/how-...", Slice(link));
        Assert.Equal("https://blog.example.com/posts/how-to-write-a-lorebook-that-remembers", link.GetProperty("features")[0].GetProperty("uri").GetString());
        var tags = facets.Where(f => f.GetProperty("features")[0].GetProperty("$type").GetString() == "app.bsky.richtext.facet#tag").ToList();
        Assert.Equal(["#worldbuilding", "#writingtips"], tags.Select(Slice));
        Assert.Equal(["worldbuilding", "writingtips"], tags.Select(t => t.GetProperty("features")[0].GetProperty("tag").GetString()));

        var external = record.GetProperty("embed");
        Assert.Equal("app.bsky.embed.external", external.GetProperty("$type").GetString());
        Assert.Equal("https://blog.example.com/posts/how-to-write-a-lorebook-that-remembers", external.GetProperty("external").GetProperty("uri").GetString());
        Assert.Equal("How to write a lorebook", external.GetProperty("external").GetProperty("title").GetString());
        Assert.Equal("Twelve entries, one world.", external.GetProperty("external").GetProperty("description").GetString());
        Assert.Equal("bafkreithumb", external.GetProperty("external").GetProperty("thumb").GetProperty("ref").GetProperty("$link").GetString());

        Assert.Equal($"at://{Did}/app.bsky.feed.post/{rkey}", result.PostId);
        Assert.Equal(new Uri($"https://bsky.app/profile/{Did}/post/{rkey}"), result.PostUrl);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task The_session_is_kept_across_posts_and_channels_and_made_again_when_it_expires()
    {
        await Channel().PostAsync(Post(key: "a") with { Card = null });
        await Channel().PostAsync(Post(key: "b") with { Card = null });
        Assert.Equal(1, _sessions);

        var expired = false;
        var inner = _host.Network.Respond;
        _host.Network.Respond = r => r.Uri.AbsolutePath.EndsWith("createRecord", StringComparison.Ordinal) && !expired && (expired = true)
            ? FakeNetwork.Json(HttpStatusCode.BadRequest, new { error = "ExpiredToken", message = "Token has expired" })
            : inner(r);
        var result = await Channel().PostAsync(Post(key: "c") with { Card = null });
        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Equal(2, _sessions);
    }

    [Fact]
    public async Task Length_counts_graphemes_and_a_post_over_300_is_refused_before_anything_is_sent()
    {
        var family = "👨‍👩‍👧‍👦";   // one grapheme, seven UTF-16 units, 25 UTF-8 bytes
        var channel = Channel();
        var fits = channel.Compose(new SocialPost { Text = string.Concat(Enumerable.Repeat("é", 300)), IdempotencyKey = "k" });
        Assert.True(fits.Fits);
        Assert.Equal(300, fits.Length);
        Assert.Equal("graphemes", fits.Unit);

        var tooMany = channel.Compose(new SocialPost { Text = string.Concat(Enumerable.Repeat("a", 301)), IdempotencyKey = "k" });
        Assert.False(tooMany.Fits);
        Assert.Equal(-1, tooMany.Remaining);

        // 120 families are 120 graphemes but 3000 bytes; one more passes the byte limit.
        Assert.True(channel.Compose(new SocialPost { Text = string.Concat(Enumerable.Repeat(family, 120)), IdempotencyKey = "k" }).Fits);
        var tooHeavy = channel.Compose(new SocialPost { Text = string.Concat(Enumerable.Repeat(family, 121)), IdempotencyKey = "k" });
        Assert.False(tooHeavy.Fits);
        Assert.Contains("bytes", tooHeavy.Problems[0], StringComparison.Ordinal);

        var refused = await channel.PostAsync(new SocialPost { Text = string.Concat(Enumerable.Repeat("a", 301)), IdempotencyKey = "k" });
        Assert.Equal(SocialFailureKind.Rejected, refused.Failure!.Kind);
        Assert.Contains("301", refused.Failure.Message, StringComparison.Ordinal);
        Assert.Empty(_host.Network.Seen);
    }

    [Fact]
    public async Task A_retry_after_a_lost_answer_reads_the_record_back_and_never_posts_twice()
    {
        // The first createRecord lands at the PDS, but its answer never arrives.
        var inner = _host.Network.Respond;
        var dropped = false;
        _host.Network.Respond = r =>
        {
            var answer = inner(r);
            if (r.Uri.AbsolutePath.EndsWith("createRecord", StringComparison.Ordinal) && !dropped)
            {
                dropped = true;
                answer.Dispose();
                throw new TaskCanceledException("The answer was lost.");
            }
            return answer;
        };
        var post = Post() with { Card = null };
        var first = await _host.Publisher.PostAsync(Channel(), post);
        Assert.Equal(SocialFailureKind.Transient, first.Failure!.Kind);
        Assert.True(first.Failure.MayHavePosted);
        Assert.Single(_records);
        Assert.Equal(SocialAttemptState.Pending, _host.Ledger.Find(post.IdempotencyKey)!.State);

        var second = await _host.Publisher.PostAsync(Channel(), post);
        Assert.True(second.Posted);
        Assert.True(second.AlreadyPosted);
        Assert.Single(_records);
        Assert.Equal(1, _host.Network.Seen.Count(s => s.Uri.AbsolutePath.EndsWith("createRecord", StringComparison.Ordinal)));
        var read = _host.Network.Seen.Single(s => s.Uri.AbsolutePath.EndsWith("getRecord", StringComparison.Ordinal));
        var q = System.Web.HttpUtility.ParseQueryString(read.Uri.Query);
        Assert.Equal(Did, q["repo"]);
        Assert.Equal("app.bsky.feed.post", q["collection"]);
        Assert.Equal(_records.Keys.Single(), q["rkey"]);
        Assert.Equal(SocialAttemptState.Posted, _host.Ledger.Find(post.IdempotencyKey)!.State);

        var third = await _host.Publisher.PostAsync(Channel(), post);
        Assert.True(third.AlreadyPosted);
        Assert.Equal(second.PostUrl, third.PostUrl);
    }

    [Fact]
    public async Task A_retry_whose_first_try_never_landed_posts_under_the_same_record_key()
    {
        var post = Post() with { Card = null };
        _host.Network.Fail = r => r.Uri.AbsolutePath.EndsWith("createRecord", StringComparison.Ordinal) && _host.Network.Seen.Count(s => s.Uri.AbsolutePath.EndsWith("createRecord", StringComparison.Ordinal)) == 1
            ? new TaskCanceledException("timeout")
            : null;
        var first = await _host.Publisher.PostAsync(Channel(), post);
        Assert.True(first.Failure!.MayHavePosted);
        var rkey = _host.Ledger.Find(post.IdempotencyKey)!.Token;

        var second = await _host.Publisher.PostAsync(Channel(), post);
        Assert.True(second.Posted, second.Failure?.Message);
        Assert.False(second.AlreadyPosted);
        var creates = _host.Network.Seen.Where(s => s.Uri.AbsolutePath.EndsWith("createRecord", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, creates.Count);
        Assert.All(creates, c => Assert.Equal(rkey, c.Json.GetProperty("rkey").GetString()));
        Assert.Equal([rkey], _records.Keys);
    }

    [Fact]
    public async Task A_card_picture_over_a_megabyte_leaves_the_card_without_one_and_says_so()
    {
        var post = Post() with { Card = Post().Card! with { Image = Pictures.Png(1_000_001) } };
        var result = await Channel().PostAsync(post);
        Assert.True(result.Posted);
        Assert.Contains("1,000,000", Assert.Single(result.Notes), StringComparison.Ordinal);
        Assert.DoesNotContain(_host.Network.Seen, s => s.Uri.AbsolutePath.EndsWith("uploadBlob", StringComparison.Ordinal));
        var external = _host.Network.Seen[^1].Json.GetProperty("record").GetProperty("embed").GetProperty("external");
        Assert.False(external.TryGetProperty("thumb", out _));
    }

    [Fact]
    public async Task A_card_picture_by_address_is_fetched_through_the_media_client()
    {
        _host.Media.Respond = r => FakeNetwork.Bytes(Pictures.Png(500), "image/png");
        var post = Post() with { Card = new LinkCard { Title = "T", ImageUrl = new Uri("https://cdn.example.com/og.png") } };
        var result = await Channel().PostAsync(post);
        Assert.True(result.Posted);
        Assert.Equal(new Uri("https://cdn.example.com/og.png"), Assert.Single(_host.Media.Seen).Uri);
        Assert.Equal(500, _host.Network.Seen.Single(s => s.Uri.AbsolutePath.EndsWith("uploadBlob", StringComparison.Ordinal)).Body.Length);

        var refused = await Channel().PostAsync(post with
        {
            IdempotencyKey = "other",
            Card = new LinkCard { Title = "T", ImageUrl = new Uri("http://169.254.169.254/latest/meta-data") },
        });
        Assert.True(refused.Posted);
        Assert.Contains("refused", Assert.Single(refused.Notes), StringComparison.Ordinal);
        Assert.Single(_host.Media.Seen);
    }

    [Fact]
    public async Task Failures_map_to_their_category_with_the_networks_message()
    {
        var wrong = await _host.Channels.Bluesky(new BlueskyAccount("writer.bsky.social", "wrong-pass-word")).PostAsync(Post());
        Assert.Equal(SocialFailureKind.Auth, wrong.Failure!.Kind);
        Assert.Contains("Invalid identifier or password", wrong.Failure.Message, StringComparison.Ordinal);
        Assert.False(wrong.Failure.MayHavePosted);

        _host.Network.Respond = r => r.Uri.AbsolutePath.EndsWith("createSession", StringComparison.Ordinal)
            ? Bluesky(r)
            : FakeNetwork.Json(HttpStatusCode.TooManyRequests, new { error = "RateLimitExceeded", message = "Rate Limit Exceeded" },
                ("ratelimit-reset", TestHost.Start.AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var limited = await Channel().PostAsync(Post() with { Card = null });
        Assert.Equal(SocialFailureKind.RateLimited, limited.Failure!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), limited.Failure.RetryAfter);

        _host.Network.Respond = r => r.Uri.AbsolutePath.EndsWith("createSession", StringComparison.Ordinal)
            ? Bluesky(r)
            : FakeNetwork.Json(HttpStatusCode.BadRequest, new { error = "InvalidRequest", message = "Invalid app.bsky.feed.post record: " + new string('x', 400) });
        var rejected = await Channel().PostAsync(Post() with { Card = null });
        Assert.Equal(SocialFailureKind.Rejected, rejected.Failure!.Kind);
        Assert.StartsWith("Bluesky refused the post (400): InvalidRequest: Invalid app.bsky.feed.post record", rejected.Failure.Message, StringComparison.Ordinal);
        Assert.True(rejected.Failure.Message.Length <= 300);

        _host.Network.Respond = r => r.Uri.AbsolutePath.EndsWith("createSession", StringComparison.Ordinal)
            ? Bluesky(r)
            : FakeNetwork.Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>");
        var down = await Channel().PostAsync(Post() with { Card = null });
        Assert.Equal(SocialFailureKind.Transient, down.Failure!.Kind);
        Assert.True(down.Failure.MayHavePosted);
    }

    [Fact]
    public void A_service_address_that_isnt_public_https_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _host.Channels.Bluesky(new BlueskyAccount("a", "b-c-d-e") { Service = new Uri("http://bsky.social") }));
        Assert.Throws<ArgumentException>(() => _host.Channels.Bluesky(new BlueskyAccount("a", "b-c-d-e") { Service = new Uri("https://127.0.0.1") }));
    }
}
