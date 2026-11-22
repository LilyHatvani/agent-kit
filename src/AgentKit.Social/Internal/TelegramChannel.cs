using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentKit.Social.Internal;

/// <summary>
/// Telegram, through the Bot API (https://core.telegram.org/bots/api; the limits below are its own words, as the
/// <c>@grammyjs/types</c> 5.2.0 package carries them):
/// <list type="bullet">
/// <item>Requests: <c>POST https://api.telegram.org/bot&lt;token&gt;/&lt;method&gt;</c>; answers <c>{ ok, result }</c> or
/// <c>{ ok: false, error_code, description, parameters: { retry_after, migrate_to_chat_id } }</c>.</item>
/// <item>No pictures: <c>sendMessage</c>, "1-4096 characters after entities parsing", with <c>link_preview_options</c>
/// naming the link, so Telegram previews the page.</item>
/// <item>One picture: <c>sendPhoto</c> ("at most 10 MB", width plus height at most 10000, ratio at most 20) or, for an
/// animated GIF, <c>sendAnimation</c> ("up to 50 MB"); several: <c>sendMediaGroup</c>, "2-10 items", the files as
/// <c>attach://&lt;name&gt;</c> parts, the caption on the first. A caption is "0-1024 characters".</item>
/// <item>No <c>parse_mode</c>: the text is plain, so nothing in it is markup. Telegram has no alt text for pictures; the
/// package still asks for it (the contract does) but can't send it.</item>
/// <item>No client id makes a send idempotent: a retry after a lost answer is <see cref="SocialFailureKind.Unconfirmed"/>.</item>
/// </list>
/// The address of a post in a public chat is <c>https://t.me/&lt;username&gt;/&lt;message id&gt;</c>; in a private
/// supergroup or channel, <c>https://t.me/c/&lt;id&gt;/&lt;message id&gt;</c>, which opens for its members.
/// </summary>
internal sealed class TelegramChannel : ChannelBase
{
    private const string ApiBase = "https://api.telegram.org";
    private const int MaxText = 4096;
    private const int MaxCaption = 1024;
    private const int MaxPhoto = 10 * 1000 * 1000;
    private const int MaxAnimation = 50 * 1000 * 1000;

    /// <summary>10 pictures an album, 10 MB a photo, an animation alone; no alt text exists, so its length isn't bounded by Telegram.</summary>
    private static readonly MediaRules Pictures = new(10, MaxPhoto, int.MaxValue, AnimatedGifRule.Alone, MaxAnimation);

    private readonly TelegramChat _chat;

    public TelegramChannel(SocialChannelContext context, TelegramChat chat) : base(context, [chat.BotToken, chat.BotToken[(chat.BotToken.IndexOf(':', StringComparison.Ordinal) + 1)..]])
    {
        _chat = chat;
    }

    public override string Network => SocialNetworks.Telegram;

    public override string Id => "telegram:" + _chat.Chat;

    public override SocialIdempotency Idempotency { get; } = new(SocialIdempotencyKind.None, null,
        "Telegram can't be asked whether a message was sent: a retry after a lost answer needs a person to check the chat.");

    public override SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var link = UsableLink(post);
        var text = TextRules.Assemble(post.Text, link, link?.AbsoluteUri, TextRules.NormalizeTags(post.Tags ?? []));
        var problems = new List<string>(LinkProblems(post));
        problems.AddRange(ImageProblems(post, Pictures));
        foreach (var (image, n) in post.Images.Select((x, i) => (x, i + 1)))
        {
            if (image is not { Width: { } w, Height: { } h } || w <= 0 || h <= 0) continue;
            if (w + h > 10000) problems.Add(Invariant($"Picture {n} is {w}×{h}, and Telegram takes a width and height of at most 10000 together."));
            else if (Math.Max(w, h) > 20.0 * Math.Min(w, h)) problems.Add(Invariant($"Picture {n} is {w}×{h}, longer than the 20 to 1 Telegram takes."));
        }
        var max = post.Images.Count > 0 ? MaxCaption : MaxText;
        // Telegram counts in UTF-16 code units (its entity offsets are in them), which is what string.Length counts.
        if (text.Length > max)
            problems.Add(post.Images.Count > 0
                ? $"The caption is {text.Length} characters and Telegram takes {MaxCaption} under pictures."
                : $"The post is {text.Length} characters and Telegram takes {MaxText}.");
        if (text.Length == 0 && post.Images.Count == 0) problems.Add("The post is empty.");
        return new SocialComposition(text, text.Length, max, "characters", problems);
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        if (attempt.Resuming && !attempt.ResendUnconfirmed)
            return SocialPostResult.Failed(SocialFailureKind.Unconfirmed,
                $"An earlier try began {attempt.StartedAt:u} and Telegram can't be asked whether it was sent: check the chat before sending again.");

        var images = post.Images;
        string method;
        HttpContent content;
        long bytes = 0;
        if (images.Count == 0)
        {
            method = "sendMessage";
            var body = new JsonObject { ["chat_id"] = _chat.Chat, ["text"] = composed.Text };
            if (UsableLink(post) is { } link)
                body["link_preview_options"] = new JsonObject { ["url"] = link.AbsoluteUri, ["prefer_large_media"] = true };
            content = JsonContent.Create(body);
        }
        else
        {
            var form = new MultipartFormDataContent { { new StringContent(_chat.Chat), "chat_id" } };
            if (images.Count == 1)
            {
                var image = images[0];
                var animated = NormalType(image) == "image/gif" && Media.IsAnimatedGif(image.Bytes.Span);
                method = animated ? "sendAnimation" : "sendPhoto";
                if (composed.Text.Length > 0) form.Add(new StringContent(composed.Text), "caption");
                if (image.Width is > 0 && image.Height is > 0 && animated)
                {
                    form.Add(new StringContent(image.Width.Value.ToString(CultureInfo.InvariantCulture)), "width");
                    form.Add(new StringContent(image.Height.Value.ToString(CultureInfo.InvariantCulture)), "height");
                }
                form.Add(File(image), animated ? "animation" : "photo", "picture-1." + Extension(NormalType(image)));
            }
            else
            {
                method = "sendMediaGroup";
                var media = new JsonArray();
                for (var i = 0; i < images.Count; i++)
                {
                    var item = new JsonObject { ["type"] = "photo", ["media"] = Invariant($"attach://picture{i + 1}") };
                    if (i == 0 && composed.Text.Length > 0) item["caption"] = composed.Text;
                    media.Add(item);
                }
                form.Add(new StringContent(media.ToJsonString()), "media");
                for (var i = 0; i < images.Count; i++)
                    form.Add(File(images[i]), Invariant($"picture{i + 1}"), Invariant($"picture-{i + 1}.{Extension(NormalType(images[i]))}"));
            }
            bytes = images.Sum(x => (long)x.Bytes.Length);
            content = form;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/bot{_chat.BotToken}/{method}") { Content = content };
        using var response = await SendAsync(request, ct, bytes > 0 ? SocialDefaults.UploadTimeout(bytes) : null).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var root = doc?.RootElement;
        if (!IsSuccess(response) || root is not { } r || Prop(r, "ok") is not { ValueKind: JsonValueKind.True })
        {
            if (IsSuccess(response) && root is null)
                return SocialPostResult.Failed(SocialFailureKind.Transient, "Telegram's answer couldn't be read.", mayHavePosted: true);
            return SocialPostResult.Failed(Failure(response, root));
        }

        // An album answers with every message it sent; the first is the post's.
        var result = Prop(r, "result");
        var message = result is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0 ? list[0] : result;
        if (message is not { ValueKind: JsonValueKind.Object } m || Number(m, "message_id") is not { } id || id < 1 || id != Math.Floor(id))
            return SocialPostResult.Failed(SocialFailureKind.Transient, "Telegram's answer had no message id.", mayHavePosted: true);
        var messageId = ((long)id).ToString(CultureInfo.InvariantCulture);
        var notes = new List<string>();
        var url = Address(Prop(m, "chat"), messageId);
        if (url is null) notes.Add("The chat is private and has no address a link can open, so the post has none.");
        if (images.Count > 0 && images.Any(x => !string.IsNullOrWhiteSpace(x.Alt)))
            notes.Add("Telegram has no alt text for pictures, so the descriptions weren't sent.");
        return SocialPostResult.Success(messageId, url, notes);
    }

    private static ReadOnlyMemoryContent File(SocialImage image)
    {
        var part = new ReadOnlyMemoryContent(image.Bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(NormalType(image));
        return part;
    }

    /// <summary><c>t.me/&lt;username&gt;/&lt;id&gt;</c> for a chat with a username, <c>t.me/c/&lt;id&gt;/&lt;id&gt;</c> for a supergroup or channel without one.</summary>
    private static Uri? Address(JsonElement? chat, string messageId)
    {
        if (chat is not { } c) return null;
        if (Str(c, "username") is { Length: > 0 } user && user.All(x => char.IsAsciiLetterOrDigit(x) || x == '_'))
            return new Uri($"https://t.me/{user}/{messageId}");
        var chatId = Prop(c, "id") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v) ? v.ToString(CultureInfo.InvariantCulture) : null;
        if (chatId is not null && chatId.StartsWith("-100", StringComparison.Ordinal) && chatId.Length > 4 && Str(c, "type") is "supergroup" or "channel")
            return new Uri($"https://t.me/c/{chatId[4..]}/{messageId}");
        return null;
    }

    /// <summary>
    /// Telegram's mapping: 401 is the token; 403 a bot that was removed from the chat or may not post there; "chat not
    /// found" and a group that moved to a supergroup are the chat setting; a 429 waits <c>parameters.retry_after</c>.
    /// </summary>
    private SocialFailure Failure(HttpResponseMessage response, JsonElement? root)
    {
        var description = root is { } r ? Str(r, "description") : null;
        var failure = FromStatus(response, description);
        if (root is { } e && Prop(e, "parameters") is { } p)
        {
            if (failure.Kind == SocialFailureKind.RateLimited && failure.RetryAfter is null && Number(p, "retry_after") is { } seconds and >= 0)
                return failure with { RetryAfter = Wait(seconds) };
            if (Number(p, "migrate_to_chat_id") is { } moved)
                return new SocialFailure(SocialFailureKind.Auth,
                    Clean(Invariant($"The group became a supergroup with the id {moved:0}: set the chat to it.")));
        }
        if (failure.Kind == SocialFailureKind.Rejected && description is not null
            && description.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
            return failure with { Kind = SocialFailureKind.Auth };
        return failure;
    }
}
