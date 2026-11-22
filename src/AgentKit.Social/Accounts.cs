namespace AgentKit.Social;

// The account types hold credentials, so none is a record: a record's ToString prints every property. Each ToString
// names the account and hides the secret.

/// <summary>A Bluesky account signed in with an app password (Settings, Privacy and security, App passwords).</summary>
/// <param name="identifier">The handle (<c>name.bsky.social</c>), the DID, or the account's email.</param>
/// <param name="appPassword">An app password, never the account password.</param>
public sealed class BlueskyAccount(string identifier, string appPassword)
{
    /// <summary>The handle, DID or email the session is created with.</summary>
    public string Identifier { get; } = Required(identifier, nameof(identifier));

    /// <summary>The app password.</summary>
    public string AppPassword { get; } = Required(appPassword, nameof(appPassword));

    /// <summary>The account's PDS or entryway; <c>https://bsky.social</c> serves every account hosted by Bluesky.</summary>
    public Uri Service { get; init; } = new("https://bsky.social");

    /// <summary>The app that shows the post: its URL becomes <c>&lt;it&gt;/profile/&lt;did&gt;/post/&lt;rkey&gt;</c>.</summary>
    public Uri WebApp { get; init; } = new("https://bsky.app");

    /// <inheritdoc />
    public override string ToString() => $"Bluesky {Identifier} at {Service.Host} (app password hidden)";

    internal static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
}

/// <summary>A Mastodon account: an instance and an access token with the <c>write:statuses</c> and <c>write:media</c> scopes.</summary>
/// <param name="instance">The instance's https address, e.g. <c>https://mastodon.social</c>.</param>
/// <param name="accessToken">The access token (Preferences, Development, an application's token).</param>
public sealed class MastodonAccount(Uri instance, string accessToken)
{
    /// <summary>The instance's https address.</summary>
    public Uri Instance { get; } = instance ?? throw new ArgumentNullException(nameof(instance));

    /// <summary>The access token.</summary>
    public string AccessToken { get; } = BlueskyAccount.Required(accessToken, nameof(accessToken));

    /// <summary>The instance's limit (<c>configuration.statuses.max_characters</c> at <c>/api/v2/instance</c>); 500 is Mastodon's default.</summary>
    public int MaxCharacters { get; init; } = 500;

    /// <summary>What every link counts as (<c>configuration.statuses.characters_reserved_per_url</c>); 23 is Mastodon's default.</summary>
    public int CharactersReservedPerUrl { get; init; } = 23;

    /// <summary><c>public</c>, <c>unlisted</c>, <c>private</c> or <c>direct</c>.</summary>
    public string Visibility { get; init; } = "public";

    /// <summary>Pictures per status (<c>configuration.statuses.max_media_attachments</c>); 4 is Mastodon's default.</summary>
    public int MaxMediaAttachments { get; init; } = 4;

    /// <summary>Bytes per picture (<c>configuration.media_attachments.image_size_limit</c>); 16 MiB is Mastodon's default.</summary>
    public long ImageSizeLimit { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// Bytes for an animated GIF, which the instance turns into a video (<c>configuration.media_attachments.video_size_limit</c>);
    /// 99 MiB is Mastodon's default.
    /// </summary>
    public long VideoSizeLimit { get; init; } = 99 * 1024 * 1024;

    /// <summary>How long to wait between checks on a picture the instance is still processing (answered 202).</summary>
    public TimeSpan MediaPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How many checks before a picture still processing fails the post.</summary>
    public int MediaPollAttempts { get; init; } = 30;

    /// <inheritdoc />
    public override string ToString() => $"Mastodon at {Instance.Host} (token hidden)";
}

/// <summary>A Discord incoming webhook (Server settings, Integrations, Webhooks, Copy webhook URL).</summary>
/// <param name="url">The webhook URL, <c>https://discord.com/api/webhooks/&lt;id&gt;/&lt;token&gt;</c>. The token in it is the credential.</param>
public sealed class DiscordWebhook(Uri url)
{
    /// <summary>The webhook's id (not secret).</summary>
    public string WebhookId { get; } = DiscordWebhookUrl.Parse(url).Id;

    internal string Token { get; } = DiscordWebhookUrl.Parse(url).Token;

    /// <summary>The name the message shows instead of the webhook's own; null to keep it.</summary>
    public string? Username { get; init; }

    /// <summary>The avatar the message shows instead of the webhook's own; null to keep it.</summary>
    public Uri? AvatarUrl { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"Discord webhook {WebhookId} (token hidden)";
}

/// <summary>A Discord bot posting in one channel. The bot needs View Channel, Send Messages and Embed Links there.</summary>
/// <param name="botToken">The bot's token (Developer Portal, the application, Bot).</param>
/// <param name="channelId">The channel's id (a snowflake).</param>
public sealed class DiscordBotChannel(string botToken, string channelId)
{
    /// <summary>The bot token.</summary>
    public string BotToken { get; } = BlueskyAccount.Required(botToken, nameof(botToken));

    /// <summary>The channel's id.</summary>
    public string ChannelId { get; } = ulong.TryParse(channelId, out _) ? channelId : throw new ArgumentException("A channel id is a number.", nameof(channelId));

    /// <summary>
    /// Whether a post in an announcement channel is also published to the servers that follow it (crossposted). A failed
    /// crosspost leaves the post up and adds a note.
    /// </summary>
    public bool Crosspost { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"Discord bot in channel {ChannelId} (token hidden)";
}

/// <summary>
/// An X account, in user context, one of two ways:
/// <list type="bullet">
/// <item><b>OAuth 1.0a</b> (<see cref="XAccount(string, string, string, string)"/>): the developer app's API key and
/// secret plus the posting account's access token and secret, as the developer portal hands them out for the app
/// owner's own account. They don't expire; the app needs Read and write permission before its access token is made.
/// Every request is signed (HMAC-SHA1, RFC 5849).</item>
/// <item><b>OAuth 2.0</b> (<see cref="XAccount(string)"/>): a user access token from the PKCE flow with
/// <c>tweet.read tweet.write users.read media.write</c>. It expires after two hours;
/// <see cref="SocialChannels.RefreshXTokenAsync"/> gets a new one.</item>
/// </list>
/// </summary>
public sealed class XAccount
{
    /// <summary>An OAuth 2.0 user access token.</summary>
    public XAccount(string accessToken)
    {
        AccessToken = BlueskyAccount.Required(accessToken, nameof(accessToken));
    }

    /// <summary>The four OAuth 1.0a values: the app's API key (consumer key) and secret, the account's access token and secret.</summary>
    public XAccount(string apiKey, string apiKeySecret, string accessToken, string accessTokenSecret)
    {
        ApiKey = BlueskyAccount.Required(apiKey, nameof(apiKey));
        ApiKeySecret = BlueskyAccount.Required(apiKeySecret, nameof(apiKeySecret));
        AccessToken = BlueskyAccount.Required(accessToken, nameof(accessToken));
        AccessTokenSecret = BlueskyAccount.Required(accessTokenSecret, nameof(accessTokenSecret));
    }

    /// <summary>The access token: OAuth 2.0's bearer, or OAuth 1.0a's <c>oauth_token</c>.</summary>
    public string AccessToken { get; }

    /// <summary>OAuth 1.0a: the app's API key (consumer key); null for OAuth 2.0.</summary>
    public string? ApiKey { get; }

    internal string? ApiKeySecret { get; }

    internal string? AccessTokenSecret { get; }

    /// <summary>Whether requests are signed with OAuth 1.0a rather than sent with a bearer token.</summary>
    public bool IsOAuth1 => ApiKey is not null;

    /// <summary>The weighted length a post may have: 280, more only for an X Premium account.</summary>
    public int MaxWeightedLength { get; init; } = 280;

    /// <summary>How long to wait between checks on an upload X is still processing (an animated GIF).</summary>
    public TimeSpan MediaPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How many checks before an upload still processing fails the post.</summary>
    public int MediaPollAttempts { get; init; } = 30;

    /// <inheritdoc />
    public override string ToString() => IsOAuth1 ? "X OAuth 1.0a (keys hidden)" : "X user token (hidden)";
}

/// <summary>The X app that refreshes a user's token: its OAuth 2.0 client id, and its secret for a confidential client.</summary>
/// <param name="clientId">The OAuth 2.0 client id.</param>
/// <param name="clientSecret">The client secret; null for a public client.</param>
public sealed class XClient(string clientId, string? clientSecret = null)
{
    /// <summary>The OAuth 2.0 client id.</summary>
    public string ClientId { get; } = BlueskyAccount.Required(clientId, nameof(clientId));

    /// <summary>The client secret, for a confidential client.</summary>
    public string? ClientSecret { get; } = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret;

    /// <inheritdoc />
    public override string ToString() => $"X client {ClientId}" + (ClientSecret is null ? "" : " (secret hidden)");
}

/// <summary>A refreshed X token pair. The answer may carry a new refresh token: store it, the one sent may be spent.</summary>
public sealed class XTokens
{
    internal XTokens(string accessToken, string? refreshToken, DateTimeOffset? expiresAt)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresAt = expiresAt;
    }

    /// <summary>The new access token.</summary>
    public string AccessToken { get; }

    /// <summary>The new refresh token; the one sent is no longer valid.</summary>
    public string? RefreshToken { get; }

    /// <summary>When the access token expires, when X said.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <inheritdoc />
    public override string ToString() => $"X tokens (hidden), expiring {ExpiresAt:O}";
}

/// <summary>The answer to an X token refresh: tokens, or why not.</summary>
public sealed class XTokenRefresh
{
    internal XTokenRefresh(XTokens? tokens, SocialFailure? failure)
    {
        Tokens = tokens;
        Failure = failure;
    }

    /// <summary>The new tokens; null when the refresh failed.</summary>
    public XTokens? Tokens { get; }

    /// <summary>Why the refresh failed: <see cref="SocialFailureKind.Auth"/> means the refresh token is spent or revoked and the owner connects X again.</summary>
    public SocialFailure? Failure { get; }

    /// <inheritdoc />
    public override string ToString() => Failure is null ? "X token refreshed" : $"X token refresh failed: {Failure.Kind}";
}

internal readonly record struct DiscordWebhookUrl(string Id, string Token)
{
    private static readonly string[] Hosts = ["discord.com", "discordapp.com", "canary.discord.com", "ptb.discord.com"];

    /// <summary>Takes only <c>https://discord.com/api[/v N]/webhooks/&lt;id&gt;/&lt;token&gt;</c>; the error never repeats the URL, which holds the token.</summary>
    public static DiscordWebhookUrl Parse(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        const string Shape = "A Discord webhook URL looks like https://discord.com/api/webhooks/<id>/<token>.";
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase)
            || url.UserInfo.Length > 0 || !url.IsDefaultPort)
            throw new ArgumentException(Shape, nameof(url));
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.IndexOf(parts, "webhooks");
        if (parts.Length < 3 || parts[0] != "api" || i < 1 || i > 2 || parts.Length != i + 3
            || (i == 2 && !parts[1].StartsWith('v')) || !ulong.TryParse(parts[i + 1], out _) || parts[i + 2].Length < 10)
            throw new ArgumentException(Shape, nameof(url));
        return new DiscordWebhookUrl(parts[i + 1], parts[i + 2]);
    }

    public override string ToString() => $"webhook {Id}";
}
