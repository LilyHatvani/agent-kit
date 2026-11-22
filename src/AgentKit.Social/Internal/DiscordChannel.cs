using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    private const string ApiBase = "https://discord.com/api/v10";
    private const int MaxContent = 2000;
    private const int MaxTitle = 256;
    private const int MaxDescription = 4096;
    private const int MaxFile = 8_000_000;

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
        // With a card the link is the embed's; without one it goes in the text and Discord unfurls it. Hashtags mean
        // nothing on Discord, so they are left out.
        var text = TextRules.Assemble(post.Text, post.Card is null ? post.Link : null, post.Link?.AbsoluteUri, [], includeTags: false);
        var problems = new List<string>(CardProblems(post));
        if (text.Length > MaxContent) problems.Add($"The post is {text.Length} characters and Discord takes {MaxContent}.");
        if (text.Length == 0 && post.Card is null) problems.Add("The post is empty.");
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
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
        (byte[] Bytes, string ContentType)? file = null;
        string? fileName = null;
        if (post.Card is { } card && post.Link is { } link)
        {
            var embed = new JsonObject
            {
                ["title"] = TextRules.Cut(card.Title, MaxTitle),
                ["url"] = link.AbsoluteUri,
            };
            if (card.Description.Length > 0) embed["description"] = TextRules.Cut(card.Description, MaxDescription);
            if (!card.Image.IsEmpty)
            {
                file = await LoadCardImageAsync(card, MaxFile, notes, ct).ConfigureAwait(false);
                if (file is { } f)
                {
                    fileName = "card." + Extension(f.ContentType);
                    embed["image"] = new JsonObject { ["url"] = "attachment://" + fileName };
                    var attachment = new JsonObject { ["id"] = 0, ["filename"] = fileName };
                    if (!string.IsNullOrWhiteSpace(card.ImageAlt)) attachment["description"] = TextRules.Cut(card.ImageAlt, 1024);
                    payload["attachments"] = new JsonArray(attachment);
                }
            }
            else if (card.ImageUrl is { } imageUrl)
            {
                embed["image"] = new JsonObject { ["url"] = imageUrl.AbsoluteUri };
            }
            payload["embeds"] = new JsonArray(embed);
        }

        Uri endpoint;
        if (_webhook is not null)
        {
            if (_webhook.Username is { } name) payload["username"] = name;
            if (_webhook.AvatarUrl is { } avatar) payload["avatar_url"] = avatar.AbsoluteUri;
            endpoint = new Uri($"{ApiBase}/webhooks/{_webhook.WebhookId}/{_webhook.Token}?wait=true");
        }
        else
        {
            payload["nonce"] = attempt.Token;
            payload["enforce_nonce"] = true;
            endpoint = new Uri($"{ApiBase}/channels/{_bot!.ChannelId}/messages");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (file is { } image)
        {
            var form = new MultipartFormDataContent { { new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), "payload_json" } };
            var part = new ByteArrayContent(image.Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
            form.Add(part, "files[0]", fileName!);
            request.Content = form;
        }
        else
        {
            request.Content = JsonContent.Create(payload);
        }
        Authorize(request);
        using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response)) return SocialPostResult.Failed(Failure(response, doc));
        if (doc is null || Str(doc.RootElement, "id") is not { } messageId)
            return SocialPostResult.Failed(SocialFailureKind.Transient, "Discord's answer had no message id.", mayHavePosted: true);
        var channelId = Str(doc.RootElement, "channel_id") ?? _bot?.ChannelId;

        if (_bot is { Crosspost: true })
            await CrosspostAsync(messageId, notes, ct).ConfigureAwait(false);

        Uri? url = null;
        var guild = Str(doc.RootElement, "guild_id") ?? await GuildAsync(ct).ConfigureAwait(false);
        if (guild is not null && channelId is not null) url = new Uri($"https://discord.com/channels/{guild}/{channelId}/{messageId}");
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
            using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
            if (IsSuccess(response)) return;
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            notes.Add($"The message is up but wasn't published to following servers ({Failure(response, doc).Message}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            notes.Add("The message is up but wasn't published to following servers (Discord didn't answer).");
        }
    }

    /// <summary>The server id, from <c>GET /webhooks/{id}/{token}</c> or <c>GET /channels/{id}</c>, once per channel.</summary>
    private async Task<string?> GuildAsync(CancellationToken ct)
    {
        if (_guildId is not null) return _guildId;
        using var request = new HttpRequestMessage(HttpMethod.Get, _webhook is not null
            ? $"{ApiBase}/webhooks/{_webhook.WebhookId}/{_webhook.Token}"
            : $"{ApiBase}/channels/{_bot!.ChannelId}");
        Authorize(request);
        try
        {
            using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
            if (!IsSuccess(response)) return null;
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            return _guildId = doc is null ? null : Str(doc.RootElement, "guild_id");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Discord's mapping: a dead webhook is 404 (Unknown Webhook) and means the credential is gone.</summary>
    private SocialFailure Failure(HttpResponseMessage response, JsonDocument? doc)
    {
        var message = doc is null ? null : Str(doc.RootElement, "message");
        var failure = FromStatus(response, message);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return failure with { Kind = SocialFailureKind.Auth };
        if (failure.Kind == SocialFailureKind.RateLimited && failure.RetryAfter is null && doc is not null
            && doc.RootElement.TryGetProperty("retry_after", out var ra) && ra.TryGetDouble(out var seconds))
            return failure with { RetryAfter = TimeSpan.FromSeconds(seconds) };
        return failure;
    }
}
