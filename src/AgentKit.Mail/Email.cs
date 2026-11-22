namespace AgentKit.Mail;

/// <summary>
/// One email, fully built by the host: the package sends exactly this and adds nothing to it. Addresses are
/// <c>name@example.com</c> or <c>Name &lt;name@example.com&gt;</c>; Resend checks their syntax.
/// </summary>
/// <param name="From">The sender, on a domain verified in Resend.</param>
/// <param name="To">1 to 50 recipients (Resend's limit). Every recipient sees the others: a notification goes one email per recipient.</param>
/// <param name="Subject">The subject line.</param>
public sealed record Email(string From, IReadOnlyList<string> To, string Subject)
{
    /// <summary>One email to one recipient.</summary>
    public Email(string from, string to, string subject) : this(from, [to], subject) { }

    /// <summary>The plain-text body. Left null with an <see cref="Html"/> body, Resend writes one from the HTML; <c>""</c> sends none.</summary>
    public string? Text { get; init; }

    /// <summary>The HTML body. One of <see cref="Text"/> and <see cref="Html"/> is required.</summary>
    public string? Html { get; init; }

    /// <summary>Where replies go; empty for the sender. A visitor's address belongs here, never in <see cref="Headers"/>.</summary>
    public IReadOnlyList<string> ReplyTo { get; init; } = [];

    /// <summary>
    /// Extra headers, sent as given (<see cref="OneClickUnsubscribe.Headers"/> makes the unsubscribe pair). The host
    /// chooses every name and value: never a header copied from input.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Resend's <c>Idempotency-Key</c>: 1 to 256 printable ASCII characters (no leading or trailing space), kept by Resend
    /// for 24 hours. A send with the key of one already sent answers with that email's id and sends nothing; the same key
    /// with a different email is refused.
    /// Give one to every email that may be sent again after a lost answer, e.g. <c>notification/12345</c>.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>Why an email wasn't sent.</summary>
public enum EmailFailureKind
{
    /// <summary>Resend refused the API key or the account's setup (401, 403: a revoked key, a domain not verified). Fix the settings.</summary>
    Auth,

    /// <summary>Resend asked to wait (429: requests per second, or the daily or monthly quota). <see cref="EmailFailure.RetryAfter"/> says how long, when it said.</summary>
    RateLimited,

    /// <summary>The email can't be sent as it is: Resend's message says why (an address it won't take), or the email failed the package's check before it was sent.</summary>
    Rejected,

    /// <summary>
    /// Resend couldn't be reached, had a server error, or the answer was lost. When <see cref="EmailFailure.MayHaveSent"/>
    /// is true the email may have gone: send again only with the same <see cref="Email.IdempotencyKey"/>.
    /// </summary>
    Transient,
}

/// <summary>An email that wasn't sent: a category, a message safe to show and log, and what is known about the wait.</summary>
/// <param name="Kind">The category.</param>
/// <param name="Message">What happened, in a sentence; Resend's own message where it gave one, trimmed. Never holds the API key, but may name an address.</param>
/// <param name="Status">Resend's HTTP status, or null when there was no answer.</param>
/// <param name="RetryAfter">How long Resend asked to wait, for <see cref="EmailFailureKind.RateLimited"/>.</param>
/// <param name="MayHaveSent">Whether the request may have reached Resend and been acted on (a timeout, a lost answer, a server error, a key already used).</param>
public sealed record EmailFailure(EmailFailureKind Kind, string Message, int? Status = null, TimeSpan? RetryAfter = null, bool MayHaveSent = false);

/// <summary>What became of one email: sent, with Resend's id, or failed with an <see cref="EmailFailure"/>.</summary>
public sealed record EmailResult
{
    private EmailResult() { }

    /// <summary>Whether Resend took the email.</summary>
    public bool Sent => Failure is null;

    /// <summary>Resend's id of the email (the <c>email_id</c> its webhooks carry); null when it failed.</summary>
    public string? Id { get; init; }

    /// <summary>Why it failed; null when it was sent.</summary>
    public EmailFailure? Failure { get; init; }

    /// <summary>An email Resend took.</summary>
    public static EmailResult Success(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return new() { Id = id };
    }

    /// <summary>An email that wasn't sent.</summary>
    public static EmailResult Failed(EmailFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new() { Failure = failure };
    }
}

/// <summary>The send contract. <see cref="ResendEmailSender"/> is the Resend one; a host's own (a dev outbox, a test fake) fits the same.</summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends <paramref name="email"/> once: no retry inside, no redirect followed. Expected failures come back as a result,
    /// never a throw; only the caller's own cancellation throws.
    /// </summary>
    Task<EmailResult> SendAsync(Email email, CancellationToken cancellationToken = default);
}
