using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentKit.Channels;

namespace AgentKit.Channels.Tests;

public sealed class ChannelsTests
{
    private static readonly ChannelMessage Hostile = new("approval_requested",
        "@everyone [click](https://evil.example) <!channel>", "Body with *bold* & <b>tags</b>\nsecond line",
        new Uri("https://blog.example/admin/#/inbox"), new JsonObject { ["request"] = "r1" });

    private static async Task<JsonObject> BodyAsync(HttpRequestMessage r) =>
        JsonNode.Parse(await r.Content!.ReadAsStringAsync())!.AsObject();

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456/abc-DEF_token", true)]
    [InlineData("https://discordapp.com/api/webhooks/123456/token", true)]
    [InlineData("https://discord.com/api/v10/webhooks/123456/token", true)]
    [InlineData("https://canary.discord.com/api/webhooks/1/t", true)]
    [InlineData("http://discord.com/api/webhooks/123456/token", false)]
    [InlineData("https://discord.com.evil.example/api/webhooks/123456/token", false)]
    [InlineData("https://evil.example/api/webhooks/123456/token", false)]
    [InlineData("https://discord.com/api/webhooks/abc/token", false)]
    [InlineData("https://discord.com/api/webhooks/123456", false)]
    [InlineData("https://discord.com:8443/api/webhooks/123456/token", false)]
    [InlineData("https://user@discord.com/api/webhooks/123456/token", false)]
    public void Only_a_discord_webhook_is_a_discord_webhook(string url, bool ok) =>
        Assert.Equal(ok, Discord.WebhookRefusal(new Uri(url)) is null);

    [Fact]
    public async Task Discord_mentions_nobody_escapes_the_body_and_waits_for_the_post()
    {
        using var r = Discord.Request(new Uri("https://discord.com/api/webhooks/1/t"), Hostile, "Agent Blog");
        Assert.Equal("wait=true", r.RequestUri!.Query.TrimStart('?'));
        var body = await BodyAsync(r);
        Assert.Empty(body["allowed_mentions"]!["parse"]!.AsArray());
        Assert.Equal("Agent Blog", body["username"]!.GetValue<string>());
        var embed = body["embeds"]![0]!;
        Assert.Equal("https://blog.example/admin/#/inbox", embed["url"]!.GetValue<string>());
        var description = embed["description"]!.GetValue<string>();
        Assert.Contains(@"\*bold\*", description, StringComparison.Ordinal);
        Assert.Equal(@"\@everyone \[click\]\(https://evil.example\)", Discord.Escape("@everyone [click](https://evil.example)"));

        var longTitle = Hostile with { Title = new string('x', 400), Body = new string('y', 5000) };
        using var r2 = Discord.Request(new Uri("https://discord.com/api/webhooks/1/t"), longTitle);
        var e2 = (await BodyAsync(r2))["embeds"]![0]!;
        Assert.Equal(Discord.MaxTitle, e2["title"]!.GetValue<string>().Length);
        Assert.Equal(Discord.MaxBody, e2["description"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T0/B0/xyz", true)]
    [InlineData("https://hooks.slack.com/services/", false)]
    [InlineData("https://hooks.slack.com.evil.example/services/T0/B0/xyz", false)]
    [InlineData("http://hooks.slack.com/services/T0/B0/xyz", false)]
    [InlineData("https://hooks.slack.com/workflows/T0/xyz", false)]
    public void Only_a_slack_incoming_webhook_is_one(string url, bool ok) =>
        Assert.Equal(ok, Slack.WebhookRefusal(new Uri(url)) is null);

    [Fact]
    public async Task Slack_escapes_so_agent_text_opens_no_link_and_no_mention()
    {
        using var r = Slack.Request(new Uri("https://hooks.slack.com/services/T/B/x"), Hostile);
        var body = await BodyAsync(r);
        var text = body["blocks"]![0]!["text"]!["text"]!.GetValue<string>();
        Assert.StartsWith("*<https://blog.example/admin/#/inbox|@everyone [click](https://evil.example) &lt;!channel&gt;>*\n", text, StringComparison.Ordinal);
        Assert.Contains("&amp; &lt;b&gt;tags&lt;/b&gt;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<!channel>", text, StringComparison.Ordinal);
        Assert.False(body["unfurl_links"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Ntfy_publishes_json_to_the_server_root_with_the_token_as_bearer()
    {
        using var r = Ntfy.Request(Ntfy.PublicServer, "blog-7f3a", "tk_secret", Hostile);
        Assert.Equal("https://ntfy.sh/", r.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", r.Headers.Authorization!.Scheme);
        Assert.Equal("tk_secret", r.Headers.Authorization.Parameter);
        var body = await BodyAsync(r);
        Assert.Equal("blog-7f3a", body["topic"]!.GetValue<string>());
        Assert.Equal("https://blog.example/admin/#/inbox", body["click"]!.GetValue<string>());
        using var open = Ntfy.Request(new Uri("http://ntfy.lan/"), "t", null, Hostile with { Link = null });
        Assert.Null(open.Headers.Authorization);
        Assert.Null((await BodyAsync(open))["click"]);

        Assert.NotNull(Ntfy.TopicRefusal("has space"));
        Assert.NotNull(Ntfy.TopicRefusal(new string('a', 65)));
        Assert.Throws<ArgumentException>(() => Ntfy.Request(Ntfy.PublicServer, "a/b", null, Hostile));
        Assert.Null(Ntfy.ServerRefusal(new Uri("https://ntfy.example.com")));
        Assert.NotNull(Ntfy.ServerRefusal(new Uri("https://ntfy.sh/mytopic")));
        Assert.NotNull(Ntfy.ServerRefusal(new Uri("ftp://ntfy.sh/")));
    }

    [Fact]
    public void Standard_webhooks_signs_as_the_spec_does()
    {
        // The reference vector of the Standard Webhooks libraries, recomputed independently.
        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=",
            StandardWebhooks.Sign("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", "msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, """{"test": 2432232314}"""u8));
        var secret = StandardWebhooks.NewSecret();
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);
        Assert.Equal(24, Convert.FromBase64String(secret[6..]).Length);
        Assert.Throws<ArgumentException>(() => StandardWebhooks.Sign("whsec_not base64!", "m", 1, "x"u8));
    }

    [Fact]
    public async Task A_signed_webhook_verifies_and_anything_changed_does_not()
    {
        var secret = StandardWebhooks.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        using var r = StandardWebhooks.Request(new Uri("https://hooks.example/in"), secret, "dlv_1", now, Hostile);
        var raw = await r.Content!.ReadAsByteArrayAsync();
        string H(string name) => r.Headers.GetValues(name).Single();
        Assert.Equal("dlv_1", H("webhook-id"));
        Assert.Equal("1800000000", H("webhook-timestamp"));
        Assert.True(StandardWebhooks.Verify(secret, "dlv_1", H("webhook-timestamp"), H("webhook-signature"), raw, now));
        Assert.True(StandardWebhooks.Verify(secret, "dlv_1", H("webhook-timestamp"), "v1,old " + H("webhook-signature"), raw, now.AddMinutes(4)));

        var body = JsonNode.Parse(raw)!;
        Assert.Equal("approval_requested", body["type"]!.GetValue<string>());
        Assert.Equal("r1", body["data"]!["request"]!.GetValue<string>());
        Assert.Equal(Hostile.Title, body["data"]!["title"]!.GetValue<string>()); // JSON carries the text as written

        Assert.False(StandardWebhooks.Verify(StandardWebhooks.NewSecret(), "dlv_1", H("webhook-timestamp"), H("webhook-signature"), raw, now));
        Assert.False(StandardWebhooks.Verify(secret, "dlv_2", H("webhook-timestamp"), H("webhook-signature"), raw, now));
        Assert.False(StandardWebhooks.Verify(secret, "dlv_1", H("webhook-timestamp"), H("webhook-signature"), [.. raw, 32], now));
        Assert.False(StandardWebhooks.Verify(secret, "dlv_1", H("webhook-timestamp"), H("webhook-signature"), raw, now.AddMinutes(6)));
        Assert.False(StandardWebhooks.Verify(secret, "dlv_1", "x", H("webhook-signature"), raw, now));
        Assert.NotNull(StandardWebhooks.UrlRefusal(new Uri("http://hooks.example/in")));
    }

    [Theory]
    [InlineData(200, SendResult.Sent)]
    [InlineData(204, SendResult.Sent)]
    [InlineData(429, SendResult.Retry)]
    [InlineData(408, SendResult.Retry)]
    [InlineData(500, SendResult.Retry)]
    [InlineData(503, SendResult.Retry)]
    [InlineData(401, SendResult.Gone)]
    [InlineData(403, SendResult.Gone)]
    [InlineData(404, SendResult.Gone)]
    [InlineData(410, SendResult.Gone)]
    [InlineData(400, SendResult.Failed)]
    [InlineData(413, SendResult.Failed)]
    public async Task An_answer_is_read_as_sent_retry_gone_or_failed(int status, SendResult expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"message\": \"Unknown Webhook\"}") };
        var outcome = await SendOutcome.ReadAsync(response, DateTimeOffset.UnixEpoch, default);
        Assert.Equal(expected, outcome.Result);
        Assert.Equal(status, outcome.Status);
        if (expected == SendResult.Sent) Assert.Null(outcome.Error);
        else Assert.Contains("Unknown Webhook", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_after_is_honoured_and_capped_and_a_body_nobody_can_decode_still_gives_an_error()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        using var seconds = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        seconds.Headers.TryAddWithoutValidation("Retry-After", "120");
        Assert.Equal(TimeSpan.FromMinutes(2), (await SendOutcome.ReadAsync(seconds, now, default)).RetryAfter);

        using var date = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        date.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), (await SendOutcome.ReadAsync(date, now, default)).RetryAfter);

        using var huge = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        huge.Headers.TryAddWithoutValidation("Retry-After", "999999");
        Assert.Equal(SendOutcome.MaxRetryAfter, (await SendOutcome.ReadAsync(huge, now, default)).RetryAfter);

        using var none = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal(TimeSpan.FromMinutes(1), (await SendOutcome.ReadAsync(none, now, default)).RetryAfter);

        var junk = new ByteArrayContent([0xFF, 0xFE, 0x00, 0x41, .. Encoding.UTF8.GetBytes(new string('z', 2000))]);
        junk.Headers.TryAddWithoutValidation("Content-Type", "text/plain; charset=no-such-charset");
        using var unreadable = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = junk };
        var o = await SendOutcome.ReadAsync(unreadable, now, default);
        Assert.Equal(SendResult.Failed, o.Result);
        Assert.StartsWith("400", o.Error, StringComparison.Ordinal);
        Assert.True(o.Error!.Length <= 500);
    }

    [Fact]
    public void The_retry_schedule_waits_longer_each_time_then_stops()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), Retries.Next(1));
        Assert.Equal(TimeSpan.FromHours(12), Retries.Next(5));
        Assert.Null(Retries.Next(6));
        Assert.Null(Retries.Next(0));
        Assert.Equal(TimeSpan.FromMinutes(20), Retries.Next(2, TimeSpan.FromMinutes(20)));
        Assert.Equal(TimeSpan.FromMinutes(5), Retries.Next(2, TimeSpan.FromSeconds(3)));
        Assert.Equal("ab…", Text.Truncate("abcdef", 3));
        Assert.Equal("a…", Text.Truncate("a😀bc", 3)); // never half a surrogate pair
    }
}
