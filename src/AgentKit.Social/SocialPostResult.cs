namespace AgentKit.Social;

/// <summary>Why a post didn't land.</summary>
public enum SocialFailureKind
{
    /// <summary>The credentials were refused, have expired, or lack the right to post here. Fix the channel's settings.</summary>
    Auth,

    /// <summary>The network asked to wait. <see cref="SocialFailure.RetryAfter"/> says how long, when it said.</summary>
    RateLimited,

    /// <summary>The post can't be sent as it is: too long, refused by the network (its message, trimmed), or an address that isn't allowed.</summary>
    Rejected,

    /// <summary>
    /// The network couldn't be reached or answered with a server error, or the answer was lost. When
    /// <see cref="SocialFailure.MayHavePosted"/> is true the post may be up; retry with the same idempotency key.
    /// </summary>
    Transient,

    /// <summary>
    /// An earlier attempt with this key may have posted and this network can't be asked whether it did (or the window in
    /// which it can has passed). A person checks the network; a resend is explicit (<see cref="SocialPublisher.PostAsync"/>).
    /// </summary>
    Unconfirmed,
}

/// <summary>A failed post: a category, a message safe to show and log, and how long to wait when the network said.</summary>
/// <param name="Kind">The category.</param>
/// <param name="Message">What happened, in a sentence; the network's own message where it gave one, trimmed. Never holds a credential.</param>
/// <param name="RetryAfter">How long the network asked to wait, for <see cref="SocialFailureKind.RateLimited"/>.</param>
/// <param name="MayHavePosted">Whether the request may have reached the network and been acted on (a timeout, a lost or unreadable answer, a server error).</param>
public sealed record SocialFailure(SocialFailureKind Kind, string Message, TimeSpan? RetryAfter = null, bool MayHavePosted = false);

/// <summary>What became of a post: up, with the network's id and address, or failed with a <see cref="SocialFailure"/>.</summary>
public sealed record SocialPostResult
{
    private SocialPostResult() { }

    /// <summary>Whether the post is up.</summary>
    public bool Posted => Failure is null;

    /// <summary>The network's id of the post (Bluesky: its <c>at://</c> URI; the others: the status, message or post id).</summary>
    public string? PostId { get; init; }

    /// <summary>Where people see the post; null when the network gave no way to build it (a note says so).</summary>
    public Uri? PostUrl { get; init; }

    /// <summary>Why it failed; null when it's up.</summary>
    public SocialFailure? Failure { get; init; }

    /// <summary>Whether this result was read back from an earlier attempt rather than posted by this call.</summary>
    public bool AlreadyPosted { get; init; }

    /// <summary>What was left out of a post that is up, said plainly: e.g. a card picture too large for the network.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>A post that is up.</summary>
    public static SocialPostResult Success(string postId, Uri? postUrl, IReadOnlyList<string>? notes = null, bool alreadyPosted = false) =>
        new() { PostId = postId, PostUrl = postUrl, Notes = notes ?? [], AlreadyPosted = alreadyPosted };

    /// <summary>A post that didn't land.</summary>
    public static SocialPostResult Failed(SocialFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new() { Failure = failure };
    }

    /// <summary>A post that didn't land, by category and message.</summary>
    public static SocialPostResult Failed(SocialFailureKind kind, string message, TimeSpan? retryAfter = null, bool mayHavePosted = false) =>
        Failed(new SocialFailure(kind, message, retryAfter, mayHavePosted));
}
