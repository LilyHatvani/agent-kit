namespace AgentKit.Social;

/// <summary>The networks this package posts to, as <see cref="ISocialChannel.Network"/> names them.</summary>
public static class SocialNetworks
{
    /// <summary>Bluesky, over the AT Protocol.</summary>
    public const string Bluesky = "bluesky";

    /// <summary>A Mastodon (or Mastodon API compatible) instance.</summary>
    public const string Mastodon = "mastodon";

    /// <summary>Discord, through an incoming webhook or a bot in a channel.</summary>
    public const string Discord = "discord";

    /// <summary>X, through API v2.</summary>
    public const string X = "x";
}

/// <summary>
/// A place a post goes: one account on one network. Made by <see cref="SocialChannels"/> from credentials the host
/// passes in; it keeps them in memory for its own life and stores nothing.
/// </summary>
public interface ISocialChannel
{
    /// <summary>The network, one of <see cref="SocialNetworks"/>.</summary>
    string Network { get; }

    /// <summary>
    /// A stable name for this account that holds no secret: <c>bluesky:&lt;identifier&gt;</c>,
    /// <c>mastodon:&lt;instance host&gt;</c>, <c>discord-webhook:&lt;webhook id&gt;</c>, <c>discord:&lt;channel id&gt;</c>, <c>x</c>.
    /// Mastodon's and X's don't tell two accounts on the same instance or network apart, so the host's channel id
    /// belongs in <see cref="SocialPost.IdempotencyKey"/>.
    /// </summary>
    string Id { get; }

    /// <summary>What a retry after a lost answer can and can't check on this network.</summary>
    SocialIdempotency Idempotency { get; }

    /// <summary>
    /// The text as it would be sent (with the link and the tags), its length as the network counts it, and what stops it
    /// being sent. No network call, so a draft editor can show it on every keystroke.
    /// </summary>
    SocialComposition Compose(SocialPost post);

    /// <summary>A new attempt for <paramref name="post"/>: a token never given before, which a host's ledger stores before sending.</summary>
    SocialAttempt BeginAttempt(SocialPost post);

    /// <summary>
    /// Sends <paramref name="post"/> under <paramref name="attempt"/>. When the attempt is resumed it first does what the
    /// network allows to find an earlier landing (<see cref="Idempotency"/>). Expected failures come back as a result,
    /// never thrown; only the caller's own cancellation throws.
    /// </summary>
    Task<SocialPostResult> SendAsync(SocialPost post, SocialAttempt attempt, CancellationToken cancellationToken = default);
}

/// <summary>Posting without a ledger.</summary>
public static class SocialChannelExtensions
{
    /// <summary>
    /// One fresh attempt, with nothing remembered: the network's own duplicate check (Mastodon's key, Discord's nonce)
    /// still applies, but a lost answer can't be followed up. <see cref="SocialPublisher"/> is the path that can.
    /// </summary>
    public static Task<SocialPostResult> PostAsync(this ISocialChannel channel, SocialPost post, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.SendAsync(post, channel.BeginAttempt(post), cancellationToken);
    }
}

/// <summary>An attempt at a post, as a ledger keeps it between tries.</summary>
/// <param name="Token">
/// Unique to the attempt, and the network's handle for it where the network has one: Bluesky's record key, Mastodon's
/// Idempotency-Key, a Discord bot's nonce.
/// </param>
/// <param name="StartedAt">When the attempt was first made; a network's duplicate window counts from here.</param>
public sealed record SocialAttempt(string Token, DateTimeOffset StartedAt)
{
    /// <summary>Whether an earlier try of this attempt may have reached the network, so the channel checks before sending.</summary>
    public bool Resuming { get; init; }

    /// <summary>Whether a person checked the network and asked to send again although nothing can confirm the earlier try.</summary>
    public bool ResendUnconfirmed { get; init; }
}

/// <summary>How a network lets a retry avoid posting twice.</summary>
public enum SocialIdempotencyKind
{
    /// <summary>The post's address is chosen before sending, so a retry reads it back first (Bluesky). Any time later.</summary>
    ReRead,

    /// <summary>The network drops a repeat of the same key within <see cref="SocialIdempotency.Window"/> (Mastodon, a Discord bot).</summary>
    KeyWindow,

    /// <summary>Nothing to check (a Discord webhook, X): a retry after a lost answer is <see cref="SocialFailureKind.Unconfirmed"/>.</summary>
    None,
}

/// <summary>What a channel's retry can check.</summary>
/// <param name="Kind">The mechanism.</param>
/// <param name="Window">For <see cref="SocialIdempotencyKind.KeyWindow"/>, how long after the first try a resend is still safe.</param>
/// <param name="Summary">One sentence for an operator.</param>
public sealed record SocialIdempotency(SocialIdempotencyKind Kind, TimeSpan? Window, string Summary);

/// <summary>A post as a channel would send it.</summary>
/// <param name="Text">The full text: the post's words, then the link, then the tags, as this network shows them.</param>
/// <param name="Length">The length as the network counts it (see <paramref name="Unit"/>).</param>
/// <param name="MaxLength">The most the network takes.</param>
/// <param name="Unit">What is counted: <c>graphemes</c>, <c>characters</c>, <c>weighted characters</c>.</param>
/// <param name="Problems">What stops it being sent, each a sentence; empty when it can go.</param>
public sealed record SocialComposition(string Text, int Length, int MaxLength, string Unit, IReadOnlyList<string> Problems)
{
    /// <summary>Whether it can be sent as it is.</summary>
    public bool Fits => Problems.Count == 0;

    /// <summary>How much room is left; negative when over.</summary>
    public int Remaining => MaxLength - Length;
}
