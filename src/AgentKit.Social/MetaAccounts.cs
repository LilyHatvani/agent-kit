namespace AgentKit.Social;

// Like the other account types, none is a record: a record's ToString prints every property. Each ToString names the
// account and hides the token.

/// <summary>
/// A Threads profile and its long-lived user token (Meta's Threads API, permissions <c>threads_basic</c> and
/// <c>threads_content_publish</c>). Before Meta's App Review, an app posts only for its Threads testers: the owner adds
/// the profile under App roles and accepts in Threads' settings. The token lasts 60 days;
/// <see cref="SocialChannels.RefreshThreadsTokenAsync"/> renews it.
/// </summary>
/// <param name="accessToken">The long-lived user access token.</param>
/// <param name="userId">The Threads user id, or <c>me</c> for the token's own profile.</param>
public sealed class ThreadsAccount(string accessToken, string userId = "me")
{
    /// <summary>The long-lived user access token.</summary>
    internal string AccessToken { get; } = BlueskyAccount.Required(accessToken, nameof(accessToken));

    /// <summary>The Threads user id, or <c>me</c> (not secret).</summary>
    public string UserId { get; } = ValidId(userId, nameof(userId), allowMe: true);

    /// <summary>The API's host; Meta documents both <c>graph.threads.net</c> and <c>graph.threads.com</c>.</summary>
    public Uri Host { get; init; } = new("https://graph.threads.net");

    /// <summary>How long to wait between reads of a post's container while Threads prepares it.</summary>
    public TimeSpan ContainerPollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How many times the container is read before the post is given up as not ready (nothing is published then).</summary>
    public int ContainerPollAttempts { get; init; } = 20;

    /// <inheritdoc />
    public override string ToString() => $"Threads profile {UserId} (token hidden)";

    /// <summary>An id that goes in a request path: digits (or <c>me</c> where allowed), nothing else.</summary>
    internal static string ValidId(string id, string paramName, bool allowMe)
    {
        var v = BlueskyAccount.Required(id, paramName);
        if ((allowMe && v == "me") || (v.Length <= 40 && v.All(char.IsAsciiDigit))) return v;
        throw new ArgumentException(allowMe ? "An id is the account's digits, or me." : "An id is digits.", paramName);
    }
}

/// <summary>
/// A Facebook Page and a Page access token for it (Graph API, permissions <c>pages_manage_posts</c> and
/// <c>pages_read_engagement</c>; the person who made the token has a role on the Page that may post). A Page token made
/// from a long-lived user token has no expiry date, though Facebook ends it when that person's access changes.
/// Facebook has no API to post on a person's own profile: only on a Page.
/// </summary>
/// <param name="pageId">The Page's id (digits).</param>
/// <param name="accessToken">The Page access token.</param>
public sealed class FacebookPage(string pageId, string accessToken)
{
    /// <summary>The Page's id (not secret).</summary>
    public string PageId { get; } = ThreadsAccount.ValidId(pageId, nameof(pageId), allowMe: false);

    /// <summary>The Page access token.</summary>
    internal string AccessToken { get; } = BlueskyAccount.Required(accessToken, nameof(accessToken));

    /// <summary>The Graph API version posted to; Meta retires each version about two years after its release.</summary>
    public string GraphVersion { get; init; } = "v27.0";

    /// <inheritdoc />
    public override string ToString() => $"Facebook Page {PageId} (token hidden)";
}

/// <summary>A renewed Threads token: store it in place of the old one.</summary>
public sealed class ThreadsToken
{
    internal ThreadsToken(string accessToken, DateTimeOffset? expiresAt)
    {
        AccessToken = accessToken;
        ExpiresAt = expiresAt;
    }

    /// <summary>The new long-lived token.</summary>
    public string AccessToken { get; }

    /// <summary>When it expires, when Threads said (60 days on).</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <inheritdoc />
    public override string ToString() => $"Threads token (hidden), expiring {ExpiresAt:O}";
}

/// <summary>The answer to a Threads token refresh: the token, or why not.</summary>
public sealed class ThreadsTokenRefresh
{
    internal ThreadsTokenRefresh(ThreadsToken? token, SocialFailure? failure)
    {
        Token = token;
        Failure = failure;
    }

    /// <summary>The new token; null when the refresh failed.</summary>
    public ThreadsToken? Token { get; }

    /// <summary>
    /// Why it failed: <see cref="SocialFailureKind.Auth"/> means the token has expired or was revoked and the owner makes a
    /// new one; a token less than a day old can't be refreshed yet.
    /// </summary>
    public SocialFailure? Failure { get; }

    /// <inheritdoc />
    public override string ToString() => Failure is null ? "Threads token refreshed" : $"Threads token refresh failed: {Failure.Kind}";
}
