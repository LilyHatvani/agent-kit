using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace AgentKit.Social.Tests;

/// <summary>
/// The shapes an adversarial review found escaping or racing (2026-11-22): answers the parsers didn't expect, headers
/// with nonsense in them, host input Compose took as fitting, and two callers on one key.
/// </summary>
public sealed class RobustnessTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private ISocialChannel X() => _host.Channels.X(new XAccount("x-user-access-token-0123456789"));

    private ISocialChannel Discord() => _host.Channels.Discord(new DiscordWebhook(new Uri("https://discord.com/api/webhooks/1/abcdefghijklmnop")));

    private ISocialChannel Bluesky() => _host.Channels.Bluesky(new BlueskyAccount("w.bsky.social", "app-pass-word"));

    private static SocialPost Post(string key = "k") => new() { Text = "Hello", Link = new Uri("https://blog.example.com/p"), IdempotencyKey = key };

    /// <summary>The header, its value, and the wait it gives exactly (a <see cref="TimeSpan"/> as text, or null for none).</summary>
    public static TheoryData<string, string, string, string?> RateLimitHeaders => new()
    {
        { "x", "x-rate-limit-reset", "1763830000000", "00:00:00" },        // Unix milliseconds, a year before the clock: now
        { "x", "x-rate-limit-reset", "99999999999999999", "1.00:00:00" },  // far ahead: the one-day cap
        { "x", "X-RateLimit-Reset-After", "NaN", null },
        { "x", "X-RateLimit-Reset-After", "1e300", null },                 // an exponent isn't seconds
        { "bluesky", "ratelimit-reset", "99999999999999999", "1.00:00:00" },
        { "x", "Retry-After", "50000", "13:53:20" },                         // under the cap, as asked
        { "x", "Retry-After", "100000", "1.00:00:00" },                      // over it: a day exactly
    };

    [Theory]
    [MemberData(nameof(RateLimitHeaders))]
    public async Task A_rate_limit_header_with_nonsense_in_it_is_still_a_rate_limit(string network, string header, string value, string? wait)
    {
        _host.Network.Respond = r => r.Uri.AbsolutePath.EndsWith("createSession", StringComparison.Ordinal)
            ? FakeNetwork.Json(HttpStatusCode.OK, new { accessJwt = "a-jwt", refreshJwt = "r-jwt", handle = "w.bsky.social", did = "did:plc:x" })
            : FakeNetwork.Json(HttpStatusCode.TooManyRequests, new { title = "Too Many Requests", error = "RateLimitExceeded" }, (header, value));
        var channel = network == "x" ? X() : Bluesky();
        var result = await channel.PostAsync(Post());
        Assert.Equal(SocialFailureKind.RateLimited, result.Failure!.Kind);
        Assert.Equal(wait is null ? null : TimeSpan.Parse(wait, System.Globalization.CultureInfo.InvariantCulture), result.Failure.RetryAfter);
    }

    /// <summary>A Retry-After that isn't seconds or a date is no header: the network's own reset header still decides.</summary>
    [Theory]
    [InlineData("-5")]
    [InlineData("1e3")]
    [InlineData("-0.5")]
    public async Task A_malformed_retry_after_never_overrides_the_reset_header(string value)
    {
        var reset = TestHost.Start.AddMinutes(10).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.TooManyRequests, new { title = "Too Many Requests" },
            ("Retry-After", value), ("x-rate-limit-reset", reset));
        var result = await X().PostAsync(Post());
        Assert.Equal(TimeSpan.FromMinutes(10), result.Failure!.RetryAfter);
    }

    /// <summary>A status read by AgentKit.Channels' one reading: 408 and 425 are worth sending again; a 404 off Discord is still a refusal.</summary>
    [Theory]
    [InlineData(408, SocialFailureKind.Transient)]
    [InlineData(425, SocialFailureKind.Transient)]
    [InlineData(404, SocialFailureKind.Rejected)]
    [InlineData(410, SocialFailureKind.Rejected)]
    public async Task A_status_off_discord_keeps_its_network_meaning(int status, SocialFailureKind kind)
    {
        _host.Network.Respond = _ => FakeNetwork.Json((HttpStatusCode)status, new { title = "No", detail = "No" });
        var result = await X().PostAsync(Post());
        Assert.Equal(kind, result.Failure!.Kind);
        Assert.False(result.Failure.MayHavePosted);
    }

    [Fact]
    public async Task An_x_answer_that_isnt_an_object_after_the_post_may_be_up_is_a_result_that_says_so()
    {
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.Created, "[]");
        var result = await X().PostAsync(Post());
        Assert.Equal(SocialFailureKind.Transient, result.Failure!.Kind);
        Assert.True(result.Failure.MayHavePosted);
    }

    [Fact]
    public async Task A_token_answer_with_expires_in_as_a_string_keeps_the_new_tokens()
    {
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.OK, "{\"access_token\":\"new-access-0001\",\"refresh_token\":\"new-refresh-0001\",\"expires_in\":\"7200\"}");
        var refreshed = await _host.Channels.RefreshXTokenAsync(new XClient("client"), "old-refresh-0001");
        Assert.Equal("new-refresh-0001", refreshed.Tokens!.RefreshToken);
        Assert.Null(refreshed.Tokens.ExpiresAt);
    }

    [Fact]
    public async Task A_discord_429_with_retry_after_as_a_string_is_a_rate_limit()
    {
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.TooManyRequests, "{\"message\":\"slow\",\"retry_after\":\"1.5\"}");
        var result = await Discord().PostAsync(Post());
        Assert.Equal(SocialFailureKind.RateLimited, result.Failure!.Kind);
    }

    [Fact]
    public async Task A_blob_answer_that_isnt_an_object_fails_the_picture_without_posting()
    {
        _host.Network.Respond = r => r.Uri.AbsolutePath.EndsWith("createSession", StringComparison.Ordinal)
            ? FakeNetwork.Json(HttpStatusCode.OK, new { accessJwt = "a-jwt", refreshJwt = "r-jwt", handle = "w.bsky.social", did = "did:plc:x" })
            : r.Uri.AbsolutePath.EndsWith("uploadBlob", StringComparison.Ordinal)
                ? FakeNetwork.Json(HttpStatusCode.OK, "[]")
                : FakeNetwork.Json(HttpStatusCode.OK, new { uri = "at://did:plc:x/app.bsky.feed.post/3k", cid = "c" });
        var result = await Bluesky().PostAsync(Post() with { Images = [new SocialImage { Bytes = Pictures.Png(20), ContentType = "image/png", Alt = "A map" }] });
        Assert.False(result.Posted);
        Assert.DoesNotContain(_host.Network.Seen, s => s.Uri.AbsolutePath.EndsWith("createRecord", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_token_with_a_line_break_is_a_result_not_a_throw()
    {
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.OK, new { accessJwt = "bad\r\njwt", refreshJwt = "r-jwt", handle = "w.bsky.social", did = "did:plc:x" });
        var result = await Bluesky().PostAsync(Post());
        Assert.False(result.Posted);
    }

    [Fact]
    public async Task A_handler_that_throws_a_plain_cancel_the_caller_didnt_ask_for_is_a_result()
    {
        _host.Network.Fail = _ => new OperationCanceledException("a resilience handler gave up");
        var result = await X().PostAsync(Post());
        Assert.Equal(SocialFailureKind.Transient, result.Failure!.Kind);
    }

    [Theory]
    [InlineData("image/png; q=1")]
    [InlineData("text/html")]
    public void A_card_picture_type_with_parameters_or_not_a_picture_is_refused_in_compose(string type)
    {
        var post = Post() with { Card = new LinkCard { Title = "T", Image = Pictures.Png(16), ImageContentType = type } };
        foreach (var channel in new[] { Bluesky(), Discord() })
            Assert.False(channel.Compose(post).Fits);
    }

    [Fact]
    public async Task A_text_cut_in_the_middle_of_an_emoji_is_counted_and_sent_on_every_network()
    {
        var post = Post() with { Text = "Cut here \uD83D" };   // a lone high surrogate
        Assert.True(X().Compose(post).Fits);
        Assert.Contains('�', X().Compose(post).Text);
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "1" } });
        Assert.True((await X().PostAsync(post)).Posted);
    }

    [Fact]
    public void A_relative_link_is_refused_in_compose_not_thrown()
    {
        var post = new SocialPost { Text = "Hello", Link = new Uri("/posts/1", UriKind.Relative), IdempotencyKey = "k" };
        foreach (var channel in new[] { X(), Discord(), Bluesky(), _host.Channels.Mastodon(new MastodonAccount(new Uri("https://m.example"), "token-1")) })
            Assert.Contains("absolute", Assert.Single(channel.Compose(post).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_late_refusal_from_one_caller_never_overwrites_the_post_another_caller_landed()
    {
        // Mastodon deduplicates by Idempotency-Key. Caller A's request is held; caller B resumes A's attempt and lands
        // it; A then comes back with a 429. The ledger must stay Posted, and a third call must not post again.
        using var release = new ManualResetEventSlim();
        var statuses = 0;
        var byKey = new Dictionary<string, string>();
        var first = true;
        _host.Network.Respond = r =>
        {
            if (first)
            {
                first = false;
                release.Wait(TimeSpan.FromSeconds(10));
                return FakeNetwork.Json(HttpStatusCode.TooManyRequests, new { error = "Too many requests" });
            }
            var key = r.Header("Idempotency-Key")!;
            lock (byKey)
                if (!byKey.TryGetValue(key, out _)) byKey[key] = (++statuses).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return FakeNetwork.Json(HttpStatusCode.OK, new { id = byKey[key], url = "https://m.example/@w/" + byKey[key] });
        };
        var channel = _host.Channels.Mastodon(new MastodonAccount(new Uri("https://m.example"), "token-1"));

        var a = Task.Run(() => _host.Publisher.PostAsync(channel, Post()));
        while (_host.Network.Seen.Count == 0) await Task.Delay(5);
        var b = await _host.Publisher.PostAsync(channel, Post());
        Assert.True(b.Posted);
        release.Set();
        var aResult = await a;

        Assert.Equal(SocialFailureKind.RateLimited, aResult.Failure!.Kind);
        Assert.Equal(SocialAttemptState.Posted, _host.Ledger.Find("k")!.State);
        var third = await _host.Publisher.PostAsync(channel, Post());
        Assert.True(third.AlreadyPosted);
        Assert.Equal(1, statuses);
    }

    /// <summary>Retries a server error, as a host's ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler()) would.</summary>
    private sealed class RetryingHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null!;
            for (var i = 0; i < 4; i++)
            {
                response = await base.SendAsync(request, cancellationToken);
                if ((int)response.StatusCode < 500) break;
            }
            return response;
        }
    }

    [Fact]
    public async Task A_hosts_default_handlers_never_reach_the_api_client_so_a_server_error_is_one_post()
    {
        using var host = new TestHost(s => s.ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler(() => new RetryingHandler())));
        host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.ServiceUnavailable, "");
        var channel = host.Channels.Discord(new DiscordWebhook(new Uri("https://discord.com/api/webhooks/1/abcdefghijklmnop")));
        var result = await channel.PostAsync(Post());
        Assert.Equal(SocialFailureKind.Transient, result.Failure!.Kind);
        Assert.Single(host.Network.Seen);
    }
}
