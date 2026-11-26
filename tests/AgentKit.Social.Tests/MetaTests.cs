using System.Net;

namespace AgentKit.Social.Tests;

/// <summary>
/// Threads and Facebook Pages as Meta documents them: Threads posts a container then publishes it
/// (https://developers.facebook.com/docs/threads/posts), reads its permalink, and renews a long-lived token with
/// th_refresh_token (https://developers.facebook.com/documentation/threads/get-started/long-lived-tokens); a Page posts
/// to /{page}/feed (https://developers.facebook.com/docs/pages-api/posts); both answer errors in the Graph envelope
/// (https://developers.facebook.com/docs/graph-api/guides/error-handling) and have no idempotency key.
/// </summary>
public sealed class MetaTests : IDisposable
{
    private const string ThreadsToken = "threads-long-lived-token-0123456789";
    private const string PageToken = "facebook-page-token-0123456789";
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private ISocialChannel Threads(int attempts = 20) => _host.Channels.Threads(new ThreadsAccount(ThreadsToken, "17841400000000001")
    {
        ContainerPollInterval = TimeSpan.FromSeconds(3),
        ContainerPollAttempts = attempts,
    });

    private ISocialChannel Page() => _host.Channels.Facebook(new FacebookPage("104000000000001", PageToken));

    private static SocialPost Post(string key = "post-1") => new()
    {
        Text = "New on the blog: a lorebook in twelve entries.",
        Link = new Uri("https://blog.example.com/posts/lorebook"),
        Tags = ["worldbuilding"],
        IdempotencyKey = key,
    };

    private static HttpResponseMessage GraphError(HttpStatusCode status, int code, string message, int? subcode = null) =>
        FakeNetwork.Json(status, new { error = new { message, type = "OAuthException", code, error_subcode = subcode, fbtrace_id = "AbC" } });

    /// <summary>Threads, answering like the API: a container, its status, the publish, the permalink.</summary>
    private void ThreadsAnswers(Func<SeenRequest, HttpResponseMessage?>? first = null) => _host.Network.Respond = r =>
    {
        if (first?.Invoke(r) is { } answer) return answer;
        var path = r.Uri.AbsolutePath;
        if (r.Method == HttpMethod.Post && path.EndsWith("/threads", StringComparison.Ordinal)) return FakeNetwork.Json(HttpStatusCode.OK, new { id = "container-1" });
        if (r.Method == HttpMethod.Get && path.EndsWith("/container-1", StringComparison.Ordinal)) return FakeNetwork.Json(HttpStatusCode.OK, new { status = "FINISHED", id = "container-1" });
        if (r.Method == HttpMethod.Post && path.EndsWith("/threads_publish", StringComparison.Ordinal)) return FakeNetwork.Json(HttpStatusCode.OK, new { id = "18000000000000001" });
        if (r.Method == HttpMethod.Get && path.EndsWith("/18000000000000001", StringComparison.Ordinal))
            return FakeNetwork.Json(HttpStatusCode.OK, new { id = "18000000000000001", permalink = "https://www.threads.com/@someone/post/AbCdEf" });
        throw new InvalidOperationException($"No answer for {r.Method} {r.Uri}.");
    };

    [Fact]
    public async Task Threads_posts_a_text_container_with_the_link_as_its_preview_then_publishes_it()
    {
        ThreadsAnswers();
        var result = await Threads().PostAsync(Post());

        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Equal("18000000000000001", result.PostId);
        Assert.Equal(new Uri("https://www.threads.com/@someone/post/AbCdEf"), result.PostUrl);
        var seen = _host.Network.Seen;
        Assert.Equal(4, seen.Count);
        var create = seen[0].Form().ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(new Uri("https://graph.threads.net/v1.0/17841400000000001/threads"), seen[0].Uri);
        Assert.Equal("TEXT", create["media_type"]);
        Assert.Equal("New on the blog: a lorebook in twelve entries.", create["text"]); // no link, no hashtags in the words
        Assert.Equal("https://blog.example.com/posts/lorebook", create["link_attachment"]);
        Assert.Equal(ThreadsToken, create["access_token"]);
        Assert.Equal("container-1", seen[2].Form().Single(kv => kv.Key == "creation_id").Value);
        Assert.EndsWith("/threads_publish", seen[2].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Threads_waits_while_the_container_is_prepared_and_gives_up_without_publishing()
    {
        var checks = 0;
        ThreadsAnswers(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith("/container-1", StringComparison.Ordinal) && ++checks < 2
            ? FakeNetwork.Json(HttpStatusCode.OK, new { status = "IN_PROGRESS" }) : null);
        var posting = Threads().PostAsync(Post());
        while (!posting.IsCompleted && checks < 1) await Task.Delay(5);
        await Task.Delay(20);
        _host.Clock.Advance(TimeSpan.FromSeconds(3));
        var result = await posting;
        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Equal(2, checks);

        // Never ready: nothing is published, and nothing may be up.
        var before = _host.Network.Seen.Count;
        _host.Network.Respond = r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/threads", StringComparison.Ordinal)
            ? FakeNetwork.Json(HttpStatusCode.OK, new { id = "container-2" })
            : FakeNetwork.Json(HttpStatusCode.OK, new { status = "ERROR", error_message = "Bad media" });
        var failed = await Threads().PostAsync(Post("post-2"));
        Assert.Equal(SocialFailureKind.Rejected, failed.Failure!.Kind);
        Assert.False(failed.Failure.MayHavePosted);
        Assert.DoesNotContain(_host.Network.Seen.Skip(before), r => r.Uri.AbsolutePath.EndsWith("/threads_publish", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hello", 5)]
    [InlineData("žluťoučký", 9)]  // letters count one each, whatever their UTF-8 length
    [InlineData("😀", 4)]          // an emoji counts its UTF-8 bytes
    [InlineData("❤", 3)]           // a symbol in the basic plane too
    public void Threads_counts_characters_and_an_emoji_by_its_bytes(string text, int length) =>
        Assert.Equal(length, Threads().Compose(new SocialPost { Text = text, IdempotencyKey = "k" }).Length);

    [Fact]
    public async Task Threads_refuses_over_500_and_a_refused_token_is_auth_with_the_token_scrubbed()
    {
        var tooLong = Threads().Compose(new SocialPost { Text = new string('a', 501), IdempotencyKey = "k" });
        Assert.False(tooLong.Fits);

        _host.Network.Respond = _ => GraphError(HttpStatusCode.BadRequest, 190, $"Error validating access token {ThreadsToken}: Session has expired", 463);
        var result = await Threads().PostAsync(Post());
        Assert.Equal(SocialFailureKind.Auth, result.Failure!.Kind);
        Assert.DoesNotContain(ThreadsToken, result.Failure.Message, StringComparison.Ordinal);
        Assert.False(result.Failure.MayHavePosted);
    }

    [Fact]
    public async Task A_threads_retry_after_a_lost_publish_is_unconfirmed()
    {
        ThreadsAnswers();
        _host.Network.Fail = r => r.Uri.AbsolutePath.EndsWith("/threads_publish", StringComparison.Ordinal) ? new TaskCanceledException("timeout") : null;
        var first = await _host.Publisher.PostAsync(Threads(), Post());
        Assert.True(first.Failure!.MayHavePosted);
        var before = _host.Network.Seen.Count;

        var again = await _host.Publisher.PostAsync(Threads(), Post());
        Assert.Equal(SocialFailureKind.Unconfirmed, again.Failure!.Kind);
        Assert.Equal(before, _host.Network.Seen.Count);
    }

    [Fact]
    public async Task A_threads_token_is_renewed_with_th_refresh_token()
    {
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.OK, new { access_token = "threads-renewed-token-987654321", token_type = "bearer", expires_in = 5_184_000 });
        var refresh = await _host.Channels.RefreshThreadsTokenAsync(new ThreadsAccount(ThreadsToken));

        Assert.Null(refresh.Failure);
        Assert.Equal("threads-renewed-token-987654321", refresh.Token!.AccessToken);
        Assert.Equal(TestHost.Start.AddDays(60), refresh.Token.ExpiresAt);
        var r = Assert.Single(_host.Network.Seen);
        Assert.Equal(HttpMethod.Get, r.Method);
        Assert.Equal("/refresh_access_token", r.Uri.AbsolutePath);
        Assert.Contains("grant_type=th_refresh_token", r.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("renewed", refresh.ToString(), StringComparison.Ordinal);

        _host.Network.Respond = _ => GraphError(HttpStatusCode.BadRequest, 190, "Session has expired", 463);
        Assert.Equal(SocialFailureKind.Auth, (await _host.Channels.RefreshThreadsTokenAsync(new ThreadsAccount(ThreadsToken))).Failure!.Kind);
    }

    [Fact]
    public async Task A_page_post_is_the_words_and_the_link_on_the_feed_with_its_permalink()
    {
        _host.Network.Respond = r => r.Method == HttpMethod.Post
            ? FakeNetwork.Json(HttpStatusCode.OK, new { id = "104000000000001_900000000000001" })
            : FakeNetwork.Json(HttpStatusCode.OK, new { id = "104000000000001_900000000000001", permalink_url = "https://www.facebook.com/104000000000001/posts/900000000000001" });
        var result = await Page().PostAsync(Post());

        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Equal("104000000000001_900000000000001", result.PostId);
        Assert.Equal(new Uri("https://www.facebook.com/104000000000001/posts/900000000000001"), result.PostUrl);
        var post = _host.Network.Seen[0];
        Assert.Equal(new Uri("https://graph.facebook.com/v27.0/104000000000001/feed"), post.Uri);
        var form = post.Form().ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("New on the blog: a lorebook in twelve entries.\n\n#worldbuilding", form["message"]);
        Assert.Equal("https://blog.example.com/posts/lorebook", form["link"]);
        Assert.Equal(PageToken, form["access_token"]);
    }

    [Fact]
    public async Task A_page_post_without_its_permalink_still_has_an_address()
    {
        _host.Network.Respond = r => r.Method == HttpMethod.Post
            ? FakeNetwork.Json(HttpStatusCode.OK, new { id = "104000000000001_900000000000002" })
            : GraphError(HttpStatusCode.BadRequest, 100, "Unsupported get request");
        var result = await Page().PostAsync(Post());
        Assert.Equal(new Uri("https://www.facebook.com/104000000000001_900000000000002"), result.PostUrl);
    }

    [Theory]
    [InlineData(190, SocialFailureKind.Auth)]
    [InlineData(200, SocialFailureKind.Auth)]
    [InlineData(32, SocialFailureKind.RateLimited)]
    [InlineData(80001, SocialFailureKind.RateLimited)]
    [InlineData(368, SocialFailureKind.RateLimited)]
    [InlineData(506, SocialFailureKind.Rejected)]
    public async Task Graph_error_codes_read_as_their_category(int code, SocialFailureKind kind)
    {
        _host.Network.Respond = _ => GraphError(HttpStatusCode.BadRequest, code, "Refused");
        var result = await Page().PostAsync(Post());
        Assert.Equal(kind, result.Failure!.Kind);
        Assert.False(result.Failure.MayHavePosted);
    }

    [Theory]
    [InlineData("me")]
    [InlineData("page/../me")]
    [InlineData("12a")]
    public void A_page_id_is_digits(string id) => Assert.Throws<ArgumentException>(() => new FacebookPage(id, PageToken));

    [Fact]
    public void Account_strings_hide_the_tokens()
    {
        Assert.DoesNotContain(ThreadsToken, new ThreadsAccount(ThreadsToken).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PageToken, new FacebookPage("104000000000001", PageToken).ToString(), StringComparison.Ordinal);
        Assert.Equal("threads:me", _host.Channels.Threads(new ThreadsAccount(ThreadsToken)).Id);
        Assert.Equal("facebook:104000000000001", Page().Id);
    }
}
