using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using AgentKit.Social.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Social;

/// <summary>The names this package registers.</summary>
public static class SocialDefaults
{
    /// <summary>
    /// The HttpClient for the networks' APIs. Its primary handler connects only to public addresses on port 443
    /// (<c>AgentKit.Fetch</c>), follows no redirect and logs nothing (a Discord webhook's path is its credential).
    /// A test replaces the primary handler.
    /// </summary>
    public const string ApiHttpClientName = "AgentKit.Social";

    /// <summary>The HttpClient that fetches a card picture: public addresses on ports 80 and 443, up to three redirects, no logging.</summary>
    public const string MediaHttpClientName = "AgentKit.Social.Media";
}

internal sealed record SocialChannelContext(IHttpClientFactory Http, TimeProvider Clock, ILoggerFactory Logs, BlueskySessions Sessions);

/// <summary>
/// Makes channels from credentials the host passes in, typically just opened from its own sealed store (AgentKit.Secrets).
/// A channel lives as long as the host keeps it; nothing is written anywhere. Bluesky sessions are kept in this
/// process's memory per account, so making a channel per post doesn't sign in each time.
/// </summary>
public sealed class SocialChannels
{
    private readonly SocialChannelContext _context;

    /// <summary>Channels over <paramref name="http"/> (the clients <see cref="SocialServiceCollectionExtensions.AddSocialChannels"/> registers).</summary>
    public SocialChannels(IHttpClientFactory http, TimeProvider? clock = null, ILoggerFactory? logs = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _context = new SocialChannelContext(http, clock ?? TimeProvider.System, logs ?? NullLoggerFactory.Instance, new BlueskySessions());
    }

    /// <summary>A Bluesky account, posting <c>app.bsky.feed.post</c> records with link facets and a link card.</summary>
    public ISocialChannel Bluesky(BlueskyAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new BlueskyChannel(_context, account);
    }

    /// <summary>A Mastodon account, posting statuses with an Idempotency-Key.</summary>
    /// <exception cref="ArgumentException">The instance isn't an https address on the public internet.</exception>
    public ISocialChannel Mastodon(MastodonAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new MastodonChannel(_context, account);
    }

    /// <summary>A Discord incoming webhook, posting a message with an embed.</summary>
    public ISocialChannel Discord(DiscordWebhook webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        return new DiscordChannel(_context, webhook);
    }

    /// <summary>A Discord bot in one channel, posting a message with an embed and a nonce.</summary>
    public ISocialChannel Discord(DiscordBotChannel bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return new DiscordChannel(_context, bot);
    }

    /// <summary>An X account, posting through API v2. X's API is paid; see the package README.</summary>
    public ISocialChannel X(XAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new XChannel(_context, account);
    }

    /// <summary>
    /// Trades an X refresh token for a new pair (<c>POST https://api.x.com/2/oauth2/token</c>,
    /// https://docs.x.com/resources/fundamentals/authentication/oauth-2-0/user-access-token). The refresh token needs the
    /// <c>offline.access</c> scope; X rotates it, so store the new one before using the access token.
    /// </summary>
    public async Task<XTokenRefresh> RefreshXTokenAsync(XClient client, string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = client.ClientId,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, XChannel.TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        if (client.ClientSecret is { } secret)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(client.ClientId)}:{Uri.EscapeDataString(secret)}")));
        var probe = new XChannel(_context, new XAccount(refreshToken), [client.ClientSecret]);
        return await probe.RefreshAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Bluesky sessions by account, in memory: createSession is rate-limited per account (30 per 5 minutes).</summary>
internal sealed class BlueskySessions
{
    private readonly ConcurrentDictionary<string, BlueskySession> _sessions = new(StringComparer.Ordinal);

    public BlueskySession? Get(string key) => _sessions.GetValueOrDefault(key);

    public void Set(string key, BlueskySession session) => _sessions[key] = session;

    public void Drop(string key, BlueskySession session) => _sessions.TryRemove(new KeyValuePair<string, BlueskySession>(key, session));
}

internal sealed record BlueskySession(string AccessJwt, string Did, string Handle)
{
    public override string ToString() => $"Bluesky session for {Did} (tokens hidden)";
}
