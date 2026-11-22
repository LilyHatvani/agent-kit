using System.Net;

namespace AgentKit.Social.Tests;

public sealed class PublisherTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private ISocialChannel X() => _host.Channels.X(new XAccount("x-user-access-token-0123456789"));

    private static SocialPost Post(string key = "post-1") => new() { Text = "Hello", IdempotencyKey = key };

    [Fact]
    public async Task A_posted_key_answers_with_the_earlier_post_and_sends_nothing()
    {
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "1" } });
        var first = await _host.Publisher.PostAsync(X(), Post());
        var again = await _host.Publisher.PostAsync(X(), Post());

        Assert.True(first.Posted);
        Assert.False(first.AlreadyPosted);
        Assert.True(again.AlreadyPosted);
        Assert.Equal(first.PostUrl, again.PostUrl);
        Assert.Single(_host.Network.Seen);
    }

    [Fact]
    public async Task A_refusal_that_created_nothing_lets_the_next_call_start_afresh()
    {
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.TooManyRequests, new { title = "Too Many Requests" });
        var limited = await _host.Publisher.PostAsync(X(), Post());
        Assert.Equal(SocialFailureKind.RateLimited, limited.Failure!.Kind);
        Assert.Equal(SocialAttemptState.Failed, _host.Ledger.Find("post-1")!.State);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "2" } });
        var retried = await _host.Publisher.PostAsync(X(), Post());
        Assert.True(retried.Posted);
        Assert.Equal(2, _host.Network.Seen.Count);
    }

    [Fact]
    public async Task A_connection_that_never_opened_created_nothing_either()
    {
        _host.Network.Fail = _ => _host.Network.Seen.Count == 1 ? new HttpRequestException(HttpRequestError.ConnectionError, "refused") : null;
        var refused = await _host.Publisher.PostAsync(X(), Post());
        Assert.Equal(SocialFailureKind.Transient, refused.Failure!.Kind);
        Assert.False(refused.Failure.MayHavePosted);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "3" } });
        Assert.True((await _host.Publisher.PostAsync(X(), Post())).Posted);
    }

    [Fact]
    public async Task A_post_that_doesnt_fit_is_refused_without_a_ledger_row()
    {
        var result = await _host.Publisher.PostAsync(X(), new SocialPost { Text = new string('a', 281), IdempotencyKey = "long" });
        Assert.Equal(SocialFailureKind.Rejected, result.Failure!.Kind);
        Assert.Null(_host.Ledger.Find("long"));
        Assert.Empty(_host.Network.Seen);
    }

    [Fact]
    public async Task A_call_cancelled_before_sending_leaves_no_pending_attempt()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _host.Publisher.PostAsync(X(), Post(), cancellationToken: cts.Token));
        Assert.Null(_host.Ledger.Find("post-1"));

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "1" } });
        Assert.True((await _host.Publisher.PostAsync(X(), Post())).Posted);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "text/html")]
    [InlineData(true, "not a media type")]
    public async Task A_card_every_network_would_drop_or_choke_on_is_refused_before_anything_is_sent(bool withLink, string? imageType)
    {
        var post = new SocialPost
        {
            Text = "Hello",
            Link = withLink ? new Uri("https://blog.example.com/p") : null,
            Card = new LinkCard { Title = "T", Image = Pictures.Png(16), ImageContentType = imageType },
            IdempotencyKey = "card",
        };
        ISocialChannel[] channels =
        [
            _host.Channels.Bluesky(new BlueskyAccount("a.bsky.social", "pass-word")),
            _host.Channels.Mastodon(new MastodonAccount(new Uri("https://m.example"), "token-1") { AttachCardImage = true }),
            _host.Channels.Discord(new DiscordWebhook(new Uri("https://discord.com/api/webhooks/1/abcdefghijklmnop"))),
            X(),
        ];
        foreach (var channel in channels)
        {
            var result = await _host.Publisher.PostAsync(channel, post);
            Assert.Equal(SocialFailureKind.Rejected, result.Failure!.Kind);
        }
        Assert.Empty(_host.Network.Seen);
        Assert.Null(_host.Ledger.Find("card"));
    }

    [Fact]
    public async Task A_key_used_for_another_channel_is_a_programming_error()
    {
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "1" } });
        await _host.Publisher.PostAsync(X(), Post());
        var discord = _host.Channels.Discord(new DiscordWebhook(new Uri("https://discord.com/api/webhooks/1/abcdefghijklmnop")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.Publisher.PostAsync(discord, Post()));
    }

    [Fact]
    public async Task The_callers_cancellation_throws_and_an_expected_failure_never_does()
    {
        _host.Network.Fail = _ => new HttpRequestException("boom");
        var failed = await X().PostAsync(Post());
        Assert.False(failed.Posted);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _host.Network.Fail = null;
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "1" } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => X().PostAsync(Post(), cts.Token));
    }

    [Fact]
    public void Every_channel_names_what_its_retry_can_check()
    {
        Assert.Equal(SocialIdempotencyKind.ReRead, _host.Channels.Bluesky(new BlueskyAccount("a.bsky.social", "pass-word")).Idempotency.Kind);
        Assert.Equal(TimeSpan.FromMinutes(50), _host.Channels.Mastodon(new MastodonAccount(new Uri("https://m.example"), "token-1")).Idempotency.Window);
        Assert.Equal(SocialIdempotencyKind.KeyWindow, _host.Channels.Discord(new DiscordBotChannel("token-1", "1")).Idempotency.Kind);
        Assert.Equal(SocialIdempotencyKind.None, _host.Channels.Discord(new DiscordWebhook(new Uri("https://discord.com/api/webhooks/1/abcdefghijklmnop"))).Idempotency.Kind);
        Assert.Equal(SocialIdempotencyKind.None, X().Idempotency.Kind);
    }
}

/// <summary>Credentials never reach a log line, a result, an exception or a ToString.</summary>
public sealed class SecretsTests : IDisposable
{
    private const string AppPassword = "bsky-app-pass-SECRET1";
    private const string MastodonToken = "mastodon-SECRET2-token";
    private const string WebhookToken = "webhook-SECRET3-token-abcdef";
    private const string BotToken = "bot-SECRET4-token";
    private const string XToken = "x-SECRET5-token";
    private static readonly string[] All = [AppPassword, MastodonToken, WebhookToken, BotToken, XToken, "SECRET"];
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private IEnumerable<ISocialChannel> Channels() =>
    [
        _host.Channels.Bluesky(new BlueskyAccount("writer.bsky.social", AppPassword)),
        _host.Channels.Mastodon(new MastodonAccount(new Uri("https://mastodon.example"), MastodonToken)),
        _host.Channels.Discord(new DiscordWebhook(new Uri($"https://discord.com/api/webhooks/42/{WebhookToken}"))),
        _host.Channels.Discord(new DiscordBotChannel(BotToken, "777")),
        _host.Channels.X(new XAccount(XToken)),
    ];

    private static SocialPost Post() => new() { Text = "Hello", Link = new Uri("https://blog.example.com/p"), IdempotencyKey = "k" };

    [Fact]
    public async Task A_network_that_echoes_the_credential_gets_it_scrubbed_from_results_and_logs()
    {
        // The worst case: every network answers with an error that repeats the request, credential included.
        _host.Network.Respond = r => FakeNetwork.Json(HttpStatusCode.BadRequest, new
        {
            error = $"bad request to {r.Uri} with {r.Header("Authorization")} and {r.Text}",
            message = $"bad request to {r.Uri} with {r.Header("Authorization")} and {r.Text}",
            detail = $"bad request to {r.Uri} with {r.Header("Authorization")} and {r.Text}",
        });
        var results = new List<SocialPostResult>();
        foreach (var channel in Channels())
            results.Add(await channel.PostAsync(Post()));

        Assert.All(results, r => Assert.False(r.Posted));
        Assert.NotEmpty(_host.Logs.Lines);   // the control: the failures were logged
        Assert.Contains(_host.Logs.Lines, l => l.Contains("refused", StringComparison.Ordinal));
        foreach (var secret in All)
        {
            Assert.All(results, r => Assert.DoesNotContain(secret, r.Failure!.Message, StringComparison.Ordinal));
            Assert.All(_host.Logs.Lines, l => Assert.DoesNotContain(secret, l, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Transport_failures_carry_no_credential_either()
    {
        _host.Network.Fail = r => new HttpRequestException($"failed {r.Uri}");
        foreach (var channel in Channels())
        {
            var result = await channel.PostAsync(Post());
            Assert.Equal(SocialFailureKind.Transient, result.Failure!.Kind);
            foreach (var secret in All) Assert.DoesNotContain(secret, result.Failure.Message, StringComparison.Ordinal);
        }
        Assert.NotEmpty(_host.Logs.Lines);
        foreach (var secret in All) Assert.All(_host.Logs.Lines, l => Assert.DoesNotContain(secret, l, StringComparison.Ordinal));
    }

    [Fact]
    public void Account_types_print_without_their_secrets()
    {
        object[] accounts =
        [
            new BlueskyAccount("writer.bsky.social", AppPassword),
            new MastodonAccount(new Uri("https://mastodon.example"), MastodonToken),
            new DiscordWebhook(new Uri($"https://discord.com/api/webhooks/42/{WebhookToken}")),
            new DiscordBotChannel(BotToken, "777"),
            new XAccount(XToken),
            new XClient("client", "client-SECRET6"),
        ];
        foreach (var account in accounts)
            Assert.DoesNotContain("SECRET", account.ToString(), StringComparison.Ordinal);
        foreach (var channel in Channels())
            Assert.DoesNotContain("SECRET", channel.Id, StringComparison.Ordinal);
    }
}
