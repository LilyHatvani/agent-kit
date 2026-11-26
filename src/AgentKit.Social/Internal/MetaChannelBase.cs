using System.Text.Json;

namespace AgentKit.Social.Internal;

/// <summary>
/// What Meta's two APIs (Threads, the Facebook Graph API) share: the error envelope
/// <c>{ "error": { message, type, code, error_subcode, error_user_msg, fbtrace_id } }</c> and its codes
/// (https://developers.facebook.com/docs/graph-api/guides/error-handling): 190 an expired or invalid token, 10 and
/// 200–299 a missing permission, 4, 17, 32, 341, 613 and 80001 throttling (https://developers.facebook.com/docs/graph-api/overview/rate-limiting),
/// 368 a temporary block for a policy, 506 a duplicate post. Neither API has an idempotency key, so a retry after a lost
/// answer is <see cref="SocialFailureKind.Unconfirmed"/>.
/// </summary>
internal abstract class MetaChannelBase(SocialChannelContext context, IEnumerable<string?> secrets) : ChannelBase(context, secrets)
{
    public override SocialIdempotency Idempotency { get; } = new(SocialIdempotencyKind.None, null,
        "Meta has no idempotency key: a retry after a lost answer needs a person to check the account.");

    /// <summary>A resumed attempt: nothing can be asked, so a person checks (unless they already did and asked to send again).</summary>
    protected SocialPostResult? Unconfirmed(SocialAttempt attempt) => attempt.Resuming && !attempt.ResendUnconfirmed
        ? SocialPostResult.Failed(SocialFailureKind.Unconfirmed,
            $"An earlier try began {attempt.StartedAt:u} and {Name} can't be asked whether it posted: check the account before sending again.")
        : null;

    /// <summary>A refused answer, read from Meta's error envelope by its code, else by its status.</summary>
    protected SocialFailure GraphFailure(HttpResponseMessage response, JsonDocument? doc)
    {
        var error = doc is null ? null : Prop(doc.RootElement, "error");
        var message = error is { } e ? Str(e, "error_user_msg") ?? Str(e, "message") : null;
        var code = error is { } c ? Number(c, "code") : null;
        var subcode = error is { } s ? Number(s, "error_subcode") : null;
        var failure = FromStatus(response, message);
        var said = Clean(message);
        var detail = said.Length > 0 ? $": {said}" : ".";
        return code switch
        {
            190 => failure with { Kind = SocialFailureKind.Auth, Message = Clean($"{Name} refused the token{(subcode == 463 ? " (expired)" : "")}{detail}") },
            10 or (>= 200 and <= 299) => failure with { Kind = SocialFailureKind.Auth, Message = Clean($"{Name} says the app lacks the right to post here{detail}") },
            4 or 17 or 32 or 341 or 613 or 80001 => failure with { Kind = SocialFailureKind.RateLimited, RetryAfter = failure.RetryAfter, MayHavePosted = false },
            368 => failure with { Kind = SocialFailureKind.RateLimited, Message = Clean($"{Name} has blocked posting for a while{detail}"), MayHavePosted = false },
            506 => failure with { Kind = SocialFailureKind.Rejected, Message = Clean($"{Name} refused it as a duplicate of a recent post{detail}"), MayHavePosted = false },
            _ => failure,
        };
    }
}
