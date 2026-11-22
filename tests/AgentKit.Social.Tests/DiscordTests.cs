using System.Net;

namespace AgentKit.Social.Tests;

/// <summary>
/// Discord as its docs describe it: Execute Webhook with ?wait=true returning the message
/// (https://docs.discord.com/developers/resources/webhook), Get Webhook with Token for guild_id,
/// Create Message with nonce (≤ 25 characters) and enforce_nonce, Crosspost Message
/// (https://docs.discord.com/developers/resources/message), content ≤ 2000 and embed limits, allowed_mentions,
/// multipart uploads with payload_json and files[n] referenced as attachment://
/// (https://docs.discord.com/developers/reference), 429 bodies with retry_after
/// (https://docs.discord.com/developers/topics/rate-limits).
/// </summary>
public sealed class DiscordTests : IDisposable
{
    private const string WebhookToken = "Wb7xQ-secret-webhook-token_0123456789abcdef";
    private const string BotToken = "MTE.bot-secret-token.0123456789";
    private static readonly Uri WebhookUrl = new($"https://discord.com/api/webhooks/1234567890/{WebhookToken}");
    private readonly TestHost _host = new();

    public DiscordTests()
    {
        _host.Network.Respond = Discord;
    }

    public void Dispose() => _host.Dispose();

    private HttpResponseMessage Discord(SeenRequest r)
    {
        var path = r.Uri.AbsolutePath;
        if (path == $"/api/v10/webhooks/1234567890/{WebhookToken}" && r.Method == HttpMethod.Post)
            return FakeNetwork.Json(HttpStatusCode.OK, new { id = "555", channel_id = "777", webhook_id = "1234567890" });
        if (path == $"/api/v10/webhooks/1234567890/{WebhookToken}" && r.Method == HttpMethod.Get)
            return FakeNetwork.Json(HttpStatusCode.OK, new { id = "1234567890", guild_id = "999", channel_id = "777" });
        if (r.Header("Authorization") != "Bot " + BotToken)
            return FakeNetwork.Json(HttpStatusCode.Unauthorized, new { message = "401: Unauthorized", code = 0 });
        return path switch
        {
            "/api/v10/channels/777/messages" => FakeNetwork.Json(HttpStatusCode.OK, new { id = "556", channel_id = "777" }),
            "/api/v10/channels/777/messages/556/crosspost" => FakeNetwork.Json(HttpStatusCode.OK, new { id = "556", channel_id = "777" }),
            "/api/v10/channels/777" => FakeNetwork.Json(HttpStatusCode.OK, new { id = "777", guild_id = "999", type = 5 }),
            _ => FakeNetwork.Json(HttpStatusCode.NotFound, new { message = "Unknown Channel", code = 10003 }),
        };
    }

    private static SocialPost Post(string key = "post-1") => new()
    {
        Text = "New on the blog. @everyone should read it.",
        Link = new Uri("https://blog.example.com/posts/lorebook"),
        Card = new LinkCard { Title = "A lorebook", Description = "Twelve entries.", ImageUrl = new Uri("https://cdn.example.com/og.png") },
        Tags = ["worldbuilding"],
        IdempotencyKey = key,
    };

    [Fact]
    public async Task A_webhook_post_is_a_message_with_an_embed_that_pings_nobody()
    {
        var result = await _host.Channels.Discord(new DiscordWebhook(WebhookUrl) { Username = "Blog" }).PostAsync(Post());

        Assert.True(result.Posted, result.Failure?.Message);
        var post = _host.Network.Seen[0];
        Assert.Equal(new Uri($"https://discord.com/api/v10/webhooks/1234567890/{WebhookToken}?wait=true"), post.Uri);
        Assert.Null(post.Header("Authorization"));
        var body = post.Json;
        Assert.Equal("New on the blog. @everyone should read it.", body.GetProperty("content").GetString());
        Assert.Equal(0, body.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        Assert.Equal("Blog", body.GetProperty("username").GetString());
        var embed = Assert.Single(body.GetProperty("embeds").EnumerateArray());
        Assert.Equal("A lorebook", embed.GetProperty("title").GetString());
        Assert.Equal("https://blog.example.com/posts/lorebook", embed.GetProperty("url").GetString());
        Assert.Equal("Twelve entries.", embed.GetProperty("description").GetString());
        Assert.Equal("https://cdn.example.com/og.png", embed.GetProperty("image").GetProperty("url").GetString());
        Assert.False(body.TryGetProperty("nonce", out _));

        Assert.Equal("555", result.PostId);
        Assert.Equal(new Uri("https://discord.com/channels/999/777/555"), result.PostUrl);
        Assert.Empty(_host.Media.Seen);
    }

    [Fact]
    public async Task Card_picture_bytes_go_as_a_multipart_file_the_embed_points_at()
    {
        var post = Post() with { Card = new LinkCard { Title = "A lorebook", Image = Pictures.Png(64), ImageAlt = "A map" } };
        var result = await _host.Channels.Discord(new DiscordWebhook(WebhookUrl)).PostAsync(post);
        Assert.True(result.Posted, result.Failure?.Message);
        var r = _host.Network.Seen[0];
        Assert.Equal("multipart/form-data", r.ContentType);
        Assert.Contains("name=payload_json", r.Text, StringComparison.Ordinal);
        Assert.Contains("name=\"files[0]\"; filename=card.png", r.Text, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"attachment://card.png\"", r.Text, StringComparison.Ordinal);
        Assert.Contains("\"attachments\":[{\"id\":0,\"filename\":\"card.png\",\"description\":\"A map\"}]", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bot_post_carries_its_token_a_nonce_it_enforces_and_is_crossposted_when_asked()
    {
        var result = await _host.Channels.Discord(new DiscordBotChannel(BotToken, "777") { Crosspost = true }).PostAsync(Post());

        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Equal(["/api/v10/channels/777/messages", "/api/v10/channels/777/messages/556/crosspost", "/api/v10/channels/777"],
            _host.Network.Seen.Select(s => s.Uri.AbsolutePath));
        var message = _host.Network.Seen[0];
        Assert.Equal("Bot " + BotToken, message.Header("Authorization"));
        var nonce = message.Json.GetProperty("nonce").GetString()!;
        Assert.InRange(nonce.Length, 1, 25);
        Assert.True(message.Json.GetProperty("enforce_nonce").GetBoolean());
        Assert.Equal(new Uri("https://discord.com/channels/999/777/556"), result.PostUrl);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task A_bot_retry_resends_its_nonce_within_minutes_and_is_unconfirmed_after()
    {
        var bot = new DiscordBotChannel(BotToken, "777");
        _host.Network.Fail = r => _host.Network.Seen.Count == 1 ? new TaskCanceledException("timeout") : null;
        var first = await _host.Publisher.PostAsync(_host.Channels.Discord(bot), Post());
        Assert.True(first.Failure!.MayHavePosted);

        _host.Clock.Advance(TimeSpan.FromSeconds(60));
        var second = await _host.Publisher.PostAsync(_host.Channels.Discord(bot), Post());
        Assert.True(second.Posted);
        Assert.Equal(_host.Network.Seen[0].Json.GetProperty("nonce").GetString(), _host.Network.Seen[1].Json.GetProperty("nonce").GetString());

        _host.Network.Fail = r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) ? new TaskCanceledException("timeout") : null;
        await _host.Publisher.PostAsync(_host.Channels.Discord(bot), Post("post-2"));
        var sent = _host.Network.Seen.Count;
        _host.Clock.Advance(TimeSpan.FromMinutes(3));
        var late = await _host.Publisher.PostAsync(_host.Channels.Discord(bot), Post("post-2"));
        Assert.Equal(SocialFailureKind.Unconfirmed, late.Failure!.Kind);
        Assert.Equal(sent, _host.Network.Seen.Count);
    }

    [Fact]
    public async Task A_webhook_retry_after_a_lost_answer_is_unconfirmed_until_a_person_resends()
    {
        var hook = new DiscordWebhook(WebhookUrl);
        _host.Network.Fail = r => _host.Network.Seen.Count == 1 ? new TaskCanceledException("timeout") : null;
        await _host.Publisher.PostAsync(_host.Channels.Discord(hook), Post());

        var again = await _host.Publisher.PostAsync(_host.Channels.Discord(hook), Post());
        Assert.Equal(SocialFailureKind.Unconfirmed, again.Failure!.Kind);
        Assert.Single(_host.Network.Seen);

        var resent = await _host.Publisher.PostAsync(_host.Channels.Discord(hook), Post(), resendUnconfirmed: true);
        Assert.True(resent.Posted);
    }

    [Fact]
    public async Task Failures_map_to_their_category_and_a_429_takes_retry_after_from_the_body()
    {
        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.NotFound, new { message = "Unknown Webhook", code = 10015 });
        var gone = await _host.Channels.Discord(new DiscordWebhook(WebhookUrl)).PostAsync(Post());
        Assert.Equal(SocialFailureKind.Auth, gone.Failure!.Kind);
        Assert.Contains("Unknown Webhook", gone.Failure.Message, StringComparison.Ordinal);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.TooManyRequests, new { message = "You are being rate limited.", retry_after = 1.5, global = false });
        var limited = await _host.Channels.Discord(new DiscordWebhook(WebhookUrl)).PostAsync(Post());
        Assert.Equal(SocialFailureKind.RateLimited, limited.Failure!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1.5), limited.Failure.RetryAfter);

        _host.Network.Respond = _ => FakeNetwork.Json(HttpStatusCode.BadRequest, new { message = "Invalid Form Body", code = 50035 });
        var invalid = await _host.Channels.Discord(new DiscordWebhook(WebhookUrl)).PostAsync(Post());
        Assert.Equal(SocialFailureKind.Rejected, invalid.Failure!.Kind);
        Assert.Contains("Invalid Form Body", invalid.Failure.Message, StringComparison.Ordinal);

        _host.Network.Respond = Discord;
        var wrongBot = await _host.Channels.Discord(new DiscordBotChannel("wrong-token", "777")).PostAsync(Post());
        Assert.Equal(SocialFailureKind.Auth, wrongBot.Failure!.Kind);
    }

    [Fact]
    public void Content_over_2000_characters_is_refused_and_card_fields_are_cut_to_the_embed_limits()
    {
        var channel = _host.Channels.Discord(new DiscordWebhook(WebhookUrl));
        Assert.True(channel.Compose(new SocialPost { Text = new string('a', 2000), IdempotencyKey = "k" }).Fits);
        Assert.False(channel.Compose(new SocialPost { Text = new string('a', 2001), IdempotencyKey = "k" }).Fits);
        // Without a card the link goes in the text, where Discord unfurls it.
        var bare = channel.Compose(new SocialPost { Text = "Read", Link = new Uri("https://blog.example.com/p"), IdempotencyKey = "k" });
        Assert.Equal("Read\n\nhttps://blog.example.com/p", bare.Text);
    }

    [Fact]
    public async Task A_long_card_title_is_cut_to_256_with_an_ellipsis()
    {
        var post = Post() with { Card = new LinkCard { Title = new string('t', 300) } };
        await _host.Channels.Discord(new DiscordWebhook(WebhookUrl)).PostAsync(post);
        var title = _host.Network.Seen[0].Json.GetProperty("embeds")[0].GetProperty("title").GetString()!;
        Assert.Equal(256, title.Length);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/api/webhooks/1/abcdefghijklmnop")]
    [InlineData("http://discord.com/api/webhooks/1/abcdefghijklmnop")]
    [InlineData("https://discord.com/api/webhooks/notanumber/abcdefghijklmnop")]
    [InlineData("https://discord.com/api/channels/1/abcdefghijklmnop")]
    [InlineData("https://discord.com:8443/api/webhooks/1/abcdefghijklmnop")]
    public void Only_a_discord_webhook_url_is_taken_and_the_refusal_never_repeats_it(string url)
    {
        var e = Assert.Throws<ArgumentException>(() => new DiscordWebhook(new Uri(url)));
        Assert.DoesNotContain("abcdefghijklmnop", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Webhook_urls_in_every_form_discord_hands_out_are_taken()
    {
        Assert.Equal("1", new DiscordWebhook(new Uri("https://discordapp.com/api/webhooks/1/abcdefghijklmnop")).WebhookId);
        Assert.Equal("1", new DiscordWebhook(new Uri("https://discord.com/api/v10/webhooks/1/abcdefghijklmnop")).WebhookId);
        Assert.Equal("1", new DiscordWebhook(new Uri("https://canary.discord.com/api/webhooks/1/abcdefghijklmnop")).WebhookId);
    }

    private static SocialImage Picture(int n) => new() { Bytes = Pictures.Png(100 + n), ContentType = "image/png", Alt = $"Map {n}" };

    [Fact]
    public async Task Pictures_go_as_the_messages_own_attachments_in_one_request_with_no_embed()
    {
        var result = await _host.Channels.Discord(new DiscordWebhook(WebhookUrl)).PostAsync(Post() with { Images = [Picture(1), Picture(2)] });

        Assert.True(result.Posted, result.Failure?.Message);
        var r = _host.Network.Seen[0];
        Assert.Equal("multipart/form-data", r.ContentType);
        Assert.Contains("name=\"files[0]\"; filename=picture-1.png", r.Text, StringComparison.Ordinal);
        Assert.Contains("name=\"files[1]\"; filename=picture-2.png", r.Text, StringComparison.Ordinal);
        Assert.Contains("\"attachments\":[{\"id\":0,\"filename\":\"picture-1.png\",\"description\":\"Map 1\"},{\"id\":1,\"filename\":\"picture-2.png\",\"description\":\"Map 2\"}]",
            r.Text, StringComparison.Ordinal);
        Assert.Contains("\"flags\":4", r.Text, StringComparison.Ordinal);           // SUPPRESS_EMBEDS: no link card under the pictures
        Assert.DoesNotContain("\"embeds\"", r.Text, StringComparison.Ordinal);
        Assert.Contains("https://blog.example.com/posts/lorebook", r.Text, StringComparison.Ordinal);   // the link is in the text
        Assert.Empty(_host.Media.Seen);
    }

    [Fact]
    public void Picture_limits_are_discords_and_refused_in_compose()
    {
        var channel = _host.Channels.Discord(new DiscordWebhook(WebhookUrl));
        var gif = new SocialImage { Bytes = Pictures.AnimatedGif(), ContentType = "image/gif", Alt = "A spinning map" };
        Assert.True(channel.Compose(Post() with { Images = [gif, Picture(1)] }).Fits);   // Discord plays GIFs among other pictures
        Assert.Contains("at most 10", Assert.Single(channel.Compose(Post() with { Images = [.. Enumerable.Range(1, 11).Select(Picture)] }).Problems), StringComparison.Ordinal);
        Assert.Contains("1024", Assert.Single(channel.Compose(Post() with { Images = [Picture(1) with { Alt = new string('a', 1025) }] }).Problems), StringComparison.Ordinal);
        var big = Picture(1) with { Bytes = Pictures.Png(20 * 1024 * 1024 + 1) };
        Assert.Contains("bytes", Assert.Single(channel.Compose(Post() with { Images = [big] }).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_card_carries_its_accent_footer_and_time_into_the_embed()
    {
        var post = Post() with
        {
            Card = new LinkCard { Title = "📢 New feature", Accent = 0x5865F2, Footer = "Echo • New Feature", PublishedAt = TestHost.Start },
        };
        await _host.Channels.Discord(new DiscordWebhook(WebhookUrl)).PostAsync(post);
        var embed = _host.Network.Seen[0].Json.GetProperty("embeds")[0];
        Assert.Equal(0x5865F2, embed.GetProperty("color").GetInt32());
        Assert.Equal("Echo • New Feature", embed.GetProperty("footer").GetProperty("text").GetString());
        Assert.Equal("2026-11-22T12:00:00.0000000Z", embed.GetProperty("timestamp").GetString());
    }

    [Fact]
    public async Task The_multipart_payload_pings_nobody_and_suppresses_the_link_card()
    {
        var post = Post() with { Text = "Hey @everyone and <@&123>, new pictures", Images = [Picture(1)] };
        await _host.Channels.Discord(new DiscordWebhook(WebhookUrl)).PostAsync(post);
        var text = _host.Network.Seen[0].Text;
        var start = text.IndexOf("name=payload_json", StringComparison.Ordinal);
        var bodyStart = text.IndexOf("\r\n\r\n", start, StringComparison.Ordinal) + 4;
        var bodyEnd = text.IndexOf("\r\n--", bodyStart, StringComparison.Ordinal);
        using var payload = System.Text.Json.JsonDocument.Parse(text[bodyStart..bodyEnd]);
        Assert.Equal(0, payload.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        Assert.Equal(4, payload.RootElement.GetProperty("flags").GetInt32());
        Assert.Contains("@everyone", payload.RootElement.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancel_after_the_message_is_up_still_records_it_as_posted()
    {
        using var cts = new CancellationTokenSource();
        _host.Network.Respond = r =>
        {
            var answer = Discord(r);
            if (r.Method == HttpMethod.Post) cts.Cancel();   // the caller gives up right after Discord took the message
            return answer;
        };
        var hook = new DiscordWebhook(WebhookUrl);
        var result = await _host.Publisher.PostAsync(_host.Channels.Discord(hook), Post(), cancellationToken: cts.Token);
        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Equal(SocialAttemptState.Posted, _host.Ledger.Find("post-1")!.State);
        var again = await _host.Publisher.PostAsync(_host.Channels.Discord(hook), Post());
        Assert.True(again.AlreadyPosted);
    }
}
