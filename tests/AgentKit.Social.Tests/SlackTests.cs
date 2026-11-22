using System.Net;
using System.Text;

namespace AgentKit.Social.Tests;

/// <summary>
/// Slack's incoming webhook: a JSON <c>text</c>, <c>200 ok</c> as plain text, error codes in the body
/// (<c>invalid_payload</c> 400, <c>no_service</c> 404, <c>channel_is_archived</c> 410), escaping of <c>&amp; &lt; &gt;</c>.
/// </summary>
public sealed class SlackTests : IDisposable
{
    private const string Secret = "T000/B000/XXXXsecretXXXXwebhookXXXX";
    private static readonly Uri WebhookUrl = new($"https://hooks.slack.com/services/{Secret}");
    private readonly TestHost _host = new();

    public SlackTests()
    {
        _host.Network.Respond = _ => Plain(HttpStatusCode.OK, "ok");
    }

    public void Dispose() => _host.Dispose();

    private static HttpResponseMessage Plain(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        foreach (var (name, value) in headers) r.Headers.TryAddWithoutValidation(name, value);
        return r;
    }

    private ISocialChannel Channel => _host.Channels.Slack(new SlackWebhook(WebhookUrl));

    private static SocialPost Post(string key = "post-1") => new()
    {
        Text = "New: <b>Fish & chips</b> for <!channel> @here.",
        Link = new Uri("https://blog.example.com/posts/fish"),
        Card = new LinkCard { Title = "Fish" },
        Tags = ["food"],
        IdempotencyKey = key,
    };

    [Fact]
    public async Task A_post_is_one_escaped_message_whose_link_slack_previews()
    {
        var result = await Channel.PostAsync(Post());

        Assert.True(result.Posted, result.Failure?.Message);
        var r = Assert.Single(_host.Network.Seen);
        Assert.Equal(WebhookUrl, r.Uri);
        Assert.Null(r.Header("Authorization"));
        var body = r.Json;
        // The words can't open a link or a mention; the link is Slack's own link form; hashtags are left out.
        Assert.Equal("New: &lt;b&gt;Fish &amp; chips&lt;/b&gt; for &lt;!channel&gt; @here.\n\n<https://blog.example.com/posts/fish>",
            body.GetProperty("text").GetString());
        Assert.True(body.GetProperty("unfurl_links").GetBoolean());
        Assert.False(body.TryGetProperty("blocks", out _));
        Assert.Null(result.PostUrl);
        Assert.Contains(result.Notes, n => n.Contains("doesn't say where", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pictures_are_refused_rather_than_dropped()
    {
        var post = Post() with { Images = [new SocialImage { Bytes = Pictures.Png(64), ContentType = "image/png", Alt = "A plate" }] };

        Assert.False(Channel.Compose(post).Fits);
        var result = await Channel.PostAsync(post);
        Assert.Equal(SocialFailureKind.Rejected, result.Failure!.Kind);
        Assert.Contains("can't carry pictures", result.Failure.Message, StringComparison.Ordinal);
        Assert.Empty(_host.Network.Seen);
    }

    [Fact]
    public void A_text_over_slacks_limit_is_counted_and_refused()
    {
        var c = Channel.Compose(Post() with { Text = new string('a', 4001), Link = null });
        Assert.Equal(4001, c.Length);
        Assert.Contains(c.Problems, p => p.Contains("4000", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "no_service", SocialFailureKind.Auth)]
    [InlineData(HttpStatusCode.Gone, "channel_is_archived", SocialFailureKind.Auth)]
    [InlineData(HttpStatusCode.Forbidden, "action_prohibited", SocialFailureKind.Auth)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_payload", SocialFailureKind.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, "", SocialFailureKind.Transient)]
    public async Task Slacks_codes_map_to_what_the_host_must_do(HttpStatusCode status, string code, SocialFailureKind kind)
    {
        _host.Network.Respond = _ => Plain(status, code);
        var result = await Channel.PostAsync(Post());
        Assert.Equal(kind, result.Failure!.Kind);
        if (code.Length > 0) Assert.Contains(code, result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(status == HttpStatusCode.InternalServerError, result.Failure.MayHavePosted);
    }

    [Fact]
    public async Task A_rate_limit_carries_slacks_wait()
    {
        _host.Network.Respond = _ => Plain(HttpStatusCode.TooManyRequests, "rate_limited", ("Retry-After", "30"));
        var result = await Channel.PostAsync(Post());
        Assert.Equal(SocialFailureKind.RateLimited, result.Failure!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Failure.RetryAfter);
    }

    [Fact]
    public async Task A_200_without_ok_may_have_posted()
    {
        _host.Network.Respond = _ => Plain(HttpStatusCode.OK, "<html>proxy</html>");
        var result = await Channel.PostAsync(Post());
        Assert.Equal(SocialFailureKind.Transient, result.Failure!.Kind);
        Assert.True(result.Failure.MayHavePosted);
    }

    [Fact]
    public async Task A_lost_answer_is_unconfirmed_until_a_person_asks_to_resend()
    {
        _host.Network.Fail = _ => new HttpRequestException("reset", null, HttpStatusCode.OK);
        var first = await _host.Publisher.PostAsync(Channel, Post("k"));
        Assert.True(first.Failure!.MayHavePosted);

        _host.Network.Fail = null;
        var again = await _host.Publisher.PostAsync(Channel, Post("k"));
        Assert.Equal(SocialFailureKind.Unconfirmed, again.Failure!.Kind);
        Assert.Single(_host.Network.Seen);

        var resent = await _host.Publisher.PostAsync(Channel, Post("k"), resendUnconfirmed: true);
        Assert.True(resent.Posted, resent.Failure?.Message);
        Assert.Equal(2, _host.Network.Seen.Count);
    }

    [Fact]
    public async Task The_webhook_url_never_shows_in_an_id_a_message_or_a_log()
    {
        _host.Network.Respond = _ => Plain(HttpStatusCode.BadRequest, $"invalid_payload for /services/{Secret}");
        var channel = Channel;
        var result = await channel.PostAsync(Post());

        Assert.DoesNotContain(Secret, channel.Id, StringComparison.Ordinal);
        Assert.StartsWith("slack-webhook:", channel.Id, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.Logs.Lines, l => l.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, new SlackWebhook(WebhookUrl).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/services/T/B/X")]
    [InlineData("http://hooks.slack.com/services/T/B/X")]
    [InlineData("https://hooks.slack.com/workflows/T/B/X")]
    [InlineData("https://hooks.slack.com/services/T/B/X?x=1")]
    public void Only_a_slack_incoming_webhook_url_is_taken(string url) =>
        Assert.Throws<ArgumentException>(() => new SlackWebhook(new Uri(url)));
}
