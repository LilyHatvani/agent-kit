using System.Net;

namespace AgentKit.Social.Tests;

/// <summary>
/// Telegram's Bot API as its reference describes it: <c>/bot&lt;token&gt;/sendMessage</c> with <c>link_preview_options</c>,
/// <c>sendPhoto</c> / <c>sendAnimation</c> / <c>sendMediaGroup</c> as multipart with <c>attach://</c> names, text 4096 and
/// caption 1024, answers <c>{ ok, result }</c> and errors <c>{ ok: false, error_code, description, parameters }</c>.
/// </summary>
public sealed class TelegramTests : IDisposable
{
    private const string Token = "123456789:AAH-secretTokenPart_0123456789abcdef";
    private const string TokenSecretPart = "AAH-secretTokenPart_0123456789abcdef";
    private readonly TestHost _host = new();

    public TelegramTests()
    {
        _host.Network.Respond = r => Ok(r.Uri.AbsolutePath.EndsWith("/sendMediaGroup", StringComparison.Ordinal)
            ? new object[] { Message(42), Message(43) }
            : Message(42));
    }

    public void Dispose() => _host.Dispose();

    private static object Message(long id, string? username = "blogchannel", long chatId = -1001234567890, string type = "channel") =>
        new { message_id = id, chat = username is null ? (object)new { id = chatId, type } : new { id = chatId, type, username } };

    private static HttpResponseMessage Ok(object result) => FakeNetwork.Json(HttpStatusCode.OK, new { ok = true, result });

    private static HttpResponseMessage Error(HttpStatusCode status, string description, object? parameters = null) =>
        FakeNetwork.Json(status, parameters is null
            ? new { ok = false, error_code = (int)status, description }
            : (object)new { ok = false, error_code = (int)status, description, parameters });

    private ISocialChannel Channel(string chat = "@blogchannel") => _host.Channels.Telegram(new TelegramChat(Token, chat));

    private static SocialPost Post(string key = "post-1") => new()
    {
        Text = "New on the blog: <b>not markup</b>",
        Link = new Uri("https://blog.example.com/posts/lorebook"),
        Card = new LinkCard { Title = "A lorebook" },
        Tags = ["worldbuilding"],
        IdempotencyKey = key,
    };

    private static SocialImage Picture(int n = 1) => new() { Bytes = Pictures.Png(64), ContentType = "image/png", Alt = $"Map {n}" };

    [Fact]
    public async Task A_post_without_pictures_is_a_plain_message_that_previews_its_link()
    {
        var result = await Channel().PostAsync(Post());

        Assert.True(result.Posted, result.Failure?.Message);
        var r = Assert.Single(_host.Network.Seen);
        Assert.Equal(new Uri($"https://api.telegram.org/bot{Token}/sendMessage"), r.Uri);
        var body = r.Json;
        Assert.Equal("@blogchannel", body.GetProperty("chat_id").GetString());
        Assert.Equal("New on the blog: <b>not markup</b>\n\nhttps://blog.example.com/posts/lorebook\n\n#worldbuilding", body.GetProperty("text").GetString());
        Assert.False(body.TryGetProperty("parse_mode", out _));
        Assert.Equal("https://blog.example.com/posts/lorebook", body.GetProperty("link_preview_options").GetProperty("url").GetString());
        Assert.Equal("42", result.PostId);
        Assert.Equal(new Uri("https://t.me/blogchannel/42"), result.PostUrl);
    }

    [Fact]
    public async Task One_picture_is_a_photo_with_the_text_as_its_caption()
    {
        var result = await Channel().PostAsync(Post() with { Images = [Picture()] });

        Assert.True(result.Posted, result.Failure?.Message);
        var r = Assert.Single(_host.Network.Seen);
        Assert.EndsWith("/sendPhoto", r.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("multipart/form-data", r.ContentType);
        Assert.Contains("name=caption", r.Text, StringComparison.Ordinal);
        Assert.Contains("name=photo; filename=picture-1.png", r.Text, StringComparison.Ordinal);
        Assert.Contains(result.Notes, n => n.Contains("no alt text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Several_pictures_are_an_album_with_the_caption_on_the_first()
    {
        var result = await Channel().PostAsync(Post() with { Images = [Picture(1), Picture(2), Picture(3)] });

        Assert.True(result.Posted, result.Failure?.Message);
        var r = Assert.Single(_host.Network.Seen);
        Assert.EndsWith("/sendMediaGroup", r.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"media\":\"attach://picture1\",\"caption\":", r.Text, StringComparison.Ordinal);
        Assert.Contains("{\"type\":\"photo\",\"media\":\"attach://picture3\"}", r.Text, StringComparison.Ordinal);
        Assert.Contains("name=picture3; filename=picture-3.png", r.Text, StringComparison.Ordinal);
        Assert.Equal("42", result.PostId);
    }

    [Fact]
    public async Task An_animated_gif_goes_alone_as_an_animation()
    {
        var gif = new SocialImage { Bytes = Pictures.AnimatedGif(), ContentType = "image/gif", Alt = "A wave" };
        var result = await Channel().PostAsync(Post() with { Images = [gif] });
        Assert.True(result.Posted, result.Failure?.Message);
        Assert.EndsWith("/sendAnimation", _host.Network.Seen[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("name=animation", _host.Network.Seen[0].Text, StringComparison.Ordinal);

        Assert.False(Channel().Compose(Post() with { Images = [gif, Picture()] }).Fits);
    }

    [Fact]
    public void Telegrams_limits_are_checked_before_sending()
    {
        var withPicture = Channel().Compose(Post() with { Text = new string('a', 1000), Images = [Picture()] });
        Assert.Equal(1024, withPicture.MaxLength);
        Assert.Contains(withPicture.Problems, p => p.Contains("caption", StringComparison.Ordinal));

        var plain = Channel().Compose(Post() with { Text = new string('a', 1000) });
        Assert.True(plain.Fits);
        Assert.Equal(4096, plain.MaxLength);
        Assert.False(Channel().Compose(Post() with { Text = new string('a', 4097), Link = null, Tags = [] }).Fits);

        Assert.False(Channel().Compose(Post() with { Images = [.. Enumerable.Range(1, 11).Select(Picture)] }).Fits);
        Assert.False(Channel().Compose(Post() with { Images = [Picture() with { Bytes = Pictures.Png(10_000_001) }] }).Fits);
        Assert.False(Channel().Compose(Post() with { Images = [Picture() with { Width = 2100, Height = 100 }] }).Fits);
        Assert.False(Channel().Compose(Post() with { Images = [Picture() with { Width = 6000, Height = 5000 }] }).Fits);
        Assert.True(Channel().Compose(Post() with { Images = [Picture() with { Width = 5000, Height = 5000 }] }).Fits);
    }

    [Fact]
    public async Task A_flood_wait_carries_telegrams_seconds()
    {
        _host.Network.Respond = _ => Error(HttpStatusCode.TooManyRequests, "Too Many Requests: retry after 17", new { retry_after = 17 });
        var result = await Channel().PostAsync(Post());
        Assert.Equal(SocialFailureKind.RateLimited, result.Failure!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(17), result.Failure.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized", SocialFailureKind.Auth)]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden: bot was kicked from the channel chat", SocialFailureKind.Auth)]
    [InlineData(HttpStatusCode.BadRequest, "Bad Request: chat not found", SocialFailureKind.Auth)]
    [InlineData(HttpStatusCode.BadRequest, "Bad Request: message is too long", SocialFailureKind.Rejected)]
    [InlineData(HttpStatusCode.BadGateway, "Bad Gateway", SocialFailureKind.Transient)]
    public async Task Telegrams_errors_map_to_what_the_host_must_do(HttpStatusCode status, string description, SocialFailureKind kind)
    {
        _host.Network.Respond = _ => Error(status, description);
        var result = await Channel().PostAsync(Post());
        Assert.Equal(kind, result.Failure!.Kind);
        Assert.Contains(description, result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_group_that_became_a_supergroup_says_where_it_went()
    {
        _host.Network.Respond = _ => Error(HttpStatusCode.BadRequest, "Bad Request: group chat was upgraded to a supergroup chat",
            new { migrate_to_chat_id = -1009876543210 });
        var result = await Channel("-123456").PostAsync(Post());
        Assert.Equal(SocialFailureKind.Auth, result.Failure!.Kind);
        Assert.Contains("-1009876543210", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_private_channel_gets_its_members_address_and_a_private_chat_none()
    {
        _host.Network.Respond = _ => Ok(Message(7, username: null));
        var channel = await Channel("-1001234567890").PostAsync(Post());
        Assert.Equal(new Uri("https://t.me/c/1234567890/7"), channel.PostUrl);

        _host.Network.Respond = _ => Ok(Message(8, username: null, chatId: 5551234, type: "private"));
        var chat = await Channel("5551234").PostAsync(Post("post-2"));
        Assert.True(chat.Posted);
        Assert.Null(chat.PostUrl);
        Assert.Contains(chat.Notes, n => n.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lost_answer_is_unconfirmed_until_a_person_asks_to_resend()
    {
        _host.Network.Fail = _ => new TaskCanceledException("timeout");
        var first = await _host.Publisher.PostAsync(Channel(), Post("k"));
        Assert.True(first.Failure!.MayHavePosted);

        _host.Network.Fail = null;
        var again = await _host.Publisher.PostAsync(Channel(), Post("k"));
        Assert.Equal(SocialFailureKind.Unconfirmed, again.Failure!.Kind);
        Assert.Single(_host.Network.Seen);

        Assert.True((await _host.Publisher.PostAsync(Channel(), Post("k"), resendUnconfirmed: true)).Posted);
    }

    [Fact]
    public async Task The_token_never_shows_in_an_id_a_message_or_a_log()
    {
        _host.Network.Respond = r => Error(HttpStatusCode.BadRequest, $"Bad Request: {r.Uri.AbsolutePath} and {TokenSecretPart}");
        var channel = Channel();
        var result = await channel.PostAsync(Post());

        Assert.Equal("telegram:@blogchannel", channel.Id);
        Assert.DoesNotContain(TokenSecretPart, result.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.Logs.Lines, l => l.Contains(TokenSecretPart, StringComparison.Ordinal));
        Assert.DoesNotContain(TokenSecretPart, new TelegramChat(Token, "@blogchannel").ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-token", "@blogchannel")]
    [InlineData("123:abc/../x", "@blogchannel")]
    [InlineData(Token, "blogchannel")]
    [InlineData(Token, "@abc")]
    [InlineData(Token, "@1channel")]
    public void A_token_or_chat_in_the_wrong_shape_is_refused(string token, string chat) =>
        Assert.Throws<ArgumentException>(() => new TelegramChat(token, chat));
}
