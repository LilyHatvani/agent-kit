using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Channels;

namespace AgentKit.Social.Internal;

/// <summary>
/// Discord, by incoming webhook or by bot, one message with an embed.
/// <list type="bullet">
/// <item>Webhook: <c>POST /webhooks/{id}/{token}?wait=true</c>, which answers with the message
/// (https://docs.discord.com/developers/resources/webhook).</item>
/// <item>Bot: <c>POST /channels/{id}/messages</c> with <c>Authorization: Bot …</c>, a <c>nonce</c> (at most 25 characters)
/// and <c>enforce_nonce</c>, which returns the earlier message for a nonce seen "in the past few minutes"
/// (https://docs.discord.com/developers/resources/message); optionally crossposted
/// (<c>POST /channels/{id}/messages/{id}/crosspost</c>).</item>
/// <item>Limits: content 2000 characters; embed title 256, description 4096
/// (https://docs.discord.com/developers/resources/message).</item>
/// <item>Mentions: <c>allowed_mentions: { parse: [] }</c>, so a post's text never pings anyone.</item>
/// <item>Errors: <c>{ message, code }</c>; a 429 carries <c>retry_after</c> in seconds in the body and the headers
/// (https://docs.discord.com/developers/topics/rate-limits).</item>
/// </list>
/// The message's address needs the server id, read once from the webhook or the channel.
/// </summary>
internal sealed class DiscordChannel : ChannelBase
{
    /// <summary>Discord's versioned API, the one AgentKit.Channels' <see cref="DiscordWebhookAddress"/> points webhooks at.</summary>
    private static readonly string ApiBase = DiscordWebhookAddress.Api.AbsoluteUri.TrimEnd('/');
    private const int MaxContent = Discord.MaxContent;
    private const int MaxTitle = Discord.MaxTitle;
    private const int MaxDescription = Discord.MaxBody;
    private const int MaxFile = 20 * 1024 * 1024;
    private const int MaxAlt = Discord.MaxAttachmentDescription;
    private const int SuppressEmbeds = 1 << 2;

    /// <summary>
    /// 20 MiB a file and 25 MiB a request (https://docs.discord.com/developers/reference, /resources/message), alt text
    /// 1024; 10 files a message is Discord's long-standing limit, not stated on those pages.
    /// </summary>
    private static readonly MediaRules Pictures = new(10, MaxFile, MaxAlt, AnimatedGifRule.Allowed, MaxFile, MaxTotalBytes: 25 * 1024 * 1024);

    /// <summary>"The past few minutes": two is what a resend counts on.</summary>
    private static readonly TimeSpan NonceWindow = TimeSpan.FromMinutes(2);

    private readonly DiscordWebhook? _webhook;
    private readonly DiscordBotChannel? _bot;
    private string? _guildId;

    public DiscordChannel(SocialChannelContext context, DiscordWebhook webhook) : base(context, [webhook.Token])
    {
        _webhook = webhook;
        Idempotency = new SocialIdempotency(SocialIdempotencyKind.None, null,
            "A webhook can't be asked what it posted: a retry after a lost answer needs a person to check the channel.");
    }

    public DiscordChannel(SocialChannelContext context, DiscordBotChannel bot) : base(context, [bot.BotToken])
    {
        _bot = bot;
        Idempotency = new SocialIdempotency(SocialIdempotencyKind.KeyWindow, NonceWindow,
            "Discord returns the earlier message for a repeated nonce for a few minutes; later, a retry can't confirm the first try.");
    }

    public override string Network => SocialNetworks.Discord;

    public override string Id => _webhook is not null ? "discord-webhook:" + _webhook.WebhookId : "discord:" + _bot!.ChannelId;

    public override SocialIdempotency Idempotency { get; }

    /// <summary>A nonce Discord takes: 25 characters at most.</summary>
    public override SocialAttempt BeginAttempt(SocialPost post) =>
        new(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)), Now);

    public override SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var link = UsableLink(post);
        // With a card (and no pictures) the link is the embed's; otherwise it goes in the text. Hashtags mean nothing on
        // Discord, so they are left out.
        var cardEmbed = post.Card is not null && post.Images.Count == 0;
        var text = TextRules.Assemble(post.Text, cardEmbed ? null : link, link?.AbsoluteUri, [], includeTags: false);
        var problems = new List<string>(LinkProblems(post));
        problems.AddRange(CardProblems(post));
        problems.AddRange(ImageProblems(post, Pictures));
        if (text.Length > MaxContent) problems.Add($"The post is {text.Length} characters and Discord takes {MaxContent}.");
        if (text.Length == 0 && post.Card is null && post.Images.Count == 0) problems.Add("The post is empty.");
        return new SocialComposition(text, text.Length, MaxContent, "characters", problems);
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        if (attempt.Resuming && !attempt.ResendUnconfirmed && (_webhook is not null || Now - attempt.StartedAt > NonceWindow))
            return SocialPostResult.Failed(SocialFailureKind.Unconfirmed, _webhook is not null
                ? $"An earlier try began {attempt.StartedAt:u} and a webhook can't be asked whether it posted: check the channel before sending again."
                : $"An earlier try began {attempt.StartedAt:u}, past the minutes Discord remembers its nonce: check the channel before sending again.");

        var notes = new List<string>();
        var payload = new JsonObject
        {
            ["content"] = composed.Text,
            ["allowed_mentions"] = Discord.NoMentions(),
        };
        var files = new List<(ReadOnlyMemory<byte> Bytes, string ContentType, string Name)>();
        if (post.Images.Count > 0)
        {
            // The pictures are the message's own attachments, in one request with it, so there is never a message short
            // of a picture. SUPPRESS_EMBEDS keeps Discord from unfurling the link into a card under them.
            var attachments = new JsonArray();
            for (var i = 0; i < post.Images.Count; i++)
            {
                var image = post.Images[i];
                var type = NormalType(image);
                var name = Invariant($"picture-{i + 1}.{Extension(type)}");
                files.Add((image.Bytes, type, name));
                attachments.Add(new JsonObject { ["id"] = i, ["filename"] = name, ["description"] = image.Alt });
            }
            payload["attachments"] = attachments;
            payload["flags"] = SuppressEmbeds;
        }
        else if (post.Card is { } card && UsableLink(post) is { } link)
        {
            var embed = new JsonObject
            {
                ["title"] = Text.Truncate(card.Title, MaxTitle),
                ["url"] = link.AbsoluteUri,
            };
            if (card.Description.Length > 0) embed["description"] = Text.Truncate(card.Description, MaxDescription);
            // Echo of Dreams' announcement embed carries these three; Discord's embed object takes them as is.
            if (card.Accent is { } accent) embed["color"] = accent & 0xFFFFFF;
            if (!string.IsNullOrWhiteSpace(card.Footer)) embed["footer"] = new JsonObject { ["text"] = Text.Truncate(card.Footer, Discord.MaxFooter) };
            if (card.PublishedAt is { } at) embed["timestamp"] = at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (!card.Image.IsEmpty)
            {
                if (await LoadCardImageAsync(card, MaxFile, notes, ct).ConfigureAwait(false) is { } f)
                {
                    var name = "card." + Extension(f.ContentType);
                    files.Add((f.Bytes, f.ContentType, name));
                    embed["image"] = new JsonObject { ["url"] = "attachment://" + name };
                    var attachment = new JsonObject { ["id"] = 0, ["filename"] = name };
                    if (!string.IsNullOrWhiteSpace(card.ImageAlt)) attachment["description"] = Text.Truncate(card.ImageAlt, MaxAlt);
                    payload["attachments"] = new JsonArray(attachment);
                }
            }
            else if (card.ImageUrl is { IsAbsoluteUri: true } imageUrl)
            {
                embed["image"] = new JsonObject { ["url"] = imageUrl.AbsoluteUri };
            }
            payload["embeds"] = new JsonArray(embed);
        }

        Uri endpoint;
        if (_webhook is not null)
        {
            if (_webhook.Username is { } name) payload["username"] = Text.Truncate(name, Discord.MaxUsername);
            if (_webhook.AvatarUrl is { IsAbsoluteUri: true } avatar) payload["avatar_url"] = avatar.AbsoluteUri;
            endpoint = _webhook.Address.ExecuteUri;
        }
        else
        {
            payload["nonce"] = attempt.Token;
            payload["enforce_nonce"] = true;
            endpoint = new Uri($"{ApiBase}/channels/{_bot!.ChannelId}/messages");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (files.Count > 0)
        {
            var form = new MultipartFormDataContent { { new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), "payload_json" } };
            for (var i = 0; i < files.Count; i++)
            {
                var part = new ReadOnlyMemoryContent(files[i].Bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue(files[i].ContentType);
                form.Add(part, Invariant($"files[{i}]"), files[i].Name);
            }
            request.Content = form;
        }
        else
        {
            request.Content = JsonContent.Create(payload);
        }
        Authorize(request);
        using var response = await SendAsync(request, ct, SocialDefaults.UploadTimeout(files.Sum(f => (long)f.Bytes.Length))).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response)) return SocialPostResult.Failed(Failure(response, doc));
        if (doc is null || Str(doc.RootElement, "id") is not { } messageId)
            return SocialPostResult.Failed(SocialFailureKind.Transient, "Discord's answer had no message id.", mayHavePosted: true);
        var channelId = Str(doc.RootElement, "channel_id") ?? _bot?.ChannelId;

        // The message is up: what follows must not lose that answer, so it runs on its own short deadline rather than
        // the caller's token (a cancel now would leave the attempt pending, and a webhook could then never confirm it).
        using var after = AfterPost();
        if (_bot is { Crosspost: true })
            await CrosspostAsync(messageId, notes, after.Token).ConfigureAwait(false);

        Uri? url = null;
        var guild = Str(doc.RootElement, "guild_id") ?? await GuildAsync(after.Token).ConfigureAwait(false);
        if (guild is not null && channelId is not null && ulong.TryParse(guild, out _) && ulong.TryParse(channelId, out _) && ulong.TryParse(messageId, out _))
            url = new Uri($"https://discord.com/channels/{guild}/{channelId}/{messageId}");
        else notes.Add("Discord didn't say which server the channel is in, so the post has no address.");
        return SocialPostResult.Success(messageId, url, notes);
    }


    private void Authorize(HttpRequestMessage request)
    {
        if (_bot is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _bot.BotToken);
    }

    private async Task CrosspostAsync(string messageId, List<string> notes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/channels/{_bot!.ChannelId}/messages/{messageId}/crosspost");
        Authorize(request);
        try
        {
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            if (IsSuccess(response)) return;
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            notes.Add($"The message is up but wasn't published to following servers ({Failure(response, doc).Message}).");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            notes.Add("The message is up but wasn't published to following servers (Discord didn't answer).");
        }
    }

    /// <summary>The server id, from <c>GET /webhooks/{id}/{token}</c> or <c>GET /channels/{id}</c>, once per channel.</summary>
    private async Task<string?> GuildAsync(CancellationToken ct)
    {
        if (_guildId is not null) return _guildId;
        using var request = new HttpRequestMessage(HttpMethod.Get, _webhook is not null
            ? _webhook.Address.Uri
            : new Uri($"{ApiBase}/channels/{_bot!.ChannelId}"));
        Authorize(request);
        try
        {
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            if (!IsSuccess(response)) return null;
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            return _guildId = doc is null ? null : Str(doc.RootElement, "guild_id");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Discord's mapping: what AgentKit.Channels reads as gone (401, 403, a dead webhook's 404 Unknown Webhook, 410) means
    /// the credential no longer posts here, as it does for a notification.
    /// </summary>
    private SocialFailure Failure(HttpResponseMessage response, JsonDocument? doc)
    {
        var message = doc is null ? null : Str(doc.RootElement, "message");
        var failure = FromStatus(response, message);
        if (SendOutcome.Classify((int)response.StatusCode) == SendResult.Gone)
            return failure with { Kind = SocialFailureKind.Auth };
        if (failure.Kind == SocialFailureKind.RateLimited && failure.RetryAfter is null && doc is not null
            && Number(doc.RootElement, "retry_after") is { } seconds and >= 0)
            return failure with { RetryAfter = Wait(seconds) };
        return failure;
    }
}
