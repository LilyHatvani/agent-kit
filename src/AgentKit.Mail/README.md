# AgentKit.Mail

Send email through [Resend](https://resend.com) without its SDK, and read its delivery webhooks. Written for Agent
Blog's email notifications and agent-support; shaped from what Echo of Dreams sends today, so EoD can move onto it.

```csharp
builder.Services.AddResendEmail(config["RESEND_API_KEY"]);      // IEmailSender, unless you registered your own first

var result = await sender.SendAsync(new Email("Blog <notify@blog.example>", reader.Address, "A new comment")
{
    Text = text,
    Html = html,
    Headers = OneClickUnsubscribe.Headers(new Uri($"https://blog.example/api/unsubscribe/{token}")),
    IdempotencyKey = $"comment-notice/{commentId}/{reader.Id}",
});
if (!result.Sent && result.Failure!.Kind == EmailFailureKind.Transient) { /* retry later with the same email */ }
```

## Sending

- **`Email(From, To, Subject)`** with `Text`, `Html`, `ReplyTo`, `Headers` and `IdempotencyKey`. The package sends
  exactly what the host built: nothing is added, escaped or cut. `To` takes 1 to 50 addresses (Resend's limit) and
  every recipient sees the others, so a notification goes one email per recipient. A visitor's address goes in
  `ReplyTo`, never in a header.
- **`IEmailSender.SendAsync(email)`** is the contract; `ResendEmailSender` is the Resend one. A host's dev outbox or a
  test fake implements the same interface and is kept when registered before `AddResendEmail`.
- **One request per call.** No retry inside, no redirect followed. The JSON body is the same bytes for the same email
  (headers in ordinal order), so a retry under the same `IdempotencyKey` is the request Resend remembers for 24 hours:
  it answers with the first email's id and sends nothing. The key is 1 to 256 printable ASCII characters, not
  starting or ending with a space; Resend suggests `<event-type>/<entity-id>`.
- **The result** is sent with Resend's id (the `email_id` its webhooks carry), or failed:

  | Kind | When | |
  | --- | --- | --- |
  | `Auth` | 401, 403: the key revoked or restricted, the domain not verified | fix the settings |
  | `RateLimited` | 429: requests per second, the daily or monthly quota | `RetryAfter` from `Retry-After`, else `ratelimit-reset` (seconds), capped at 12 h |
  | `Rejected` | another 4xx with Resend's `name: message`, trimmed; or the email failed `Problems` and nothing was sent | change the email |
  | `Transient` | 5xx, 408, 425, a 409 for a key still in progress, a dropped connection, the 30 s deadline, a 2xx without an id | `MayHaveSent` says whether it may have gone |

  A 409 `invalid_idempotent_request` (the key was used for a different email) is `Rejected` with `MayHaveSent` true:
  the earlier email may be out. Expected failures never throw; only the caller's own cancellation does.
- **`ResendEmailSender.Problems(email)`** is what stops an email before it is sent: no sender, 0 or more than 50
  recipients, no body, a line break or control character in an address, a header name or value (a header injected
  through one), half of a surrogate pair anywhere (JSON would write U+FFFD), a header given twice, a key out of
  shape. No caps of its own: everything else is Resend's to refuse.
- **The key stays secret.** `AddResendEmail` refuses a key no header can carry, without repeating it. The client is
  registered with `.RemoveAllLoggers()`, every message has the key scrubbed, `ToString()` hides it, and an
  exception's own text is never used. Logs carry the kind and status only: Resend's messages can name a recipient.
- The HttpClient (`MailDefaults.ResendHttpClientName`) connects through `AgentKit.Fetch`'s guard, port 443 only, with
  a 64 KiB answer cap, and none of the host's `ConfigureHttpClientDefaults` handlers: a standard resilience handler
  would retry a 503 three times, and one send must stay one request. A handler the host adds to this client by name
  still runs (that is the host's choice). Requests carry a `User-Agent` (`AgentKit.Mail/<version>`), which Resend
  requires.

## One-click unsubscribe

`OneClickUnsubscribe.Headers(url)` gives the RFC 8058 pair, `List-Unsubscribe: <https://…>` and
`List-Unsubscribe-Post: List-Unsubscribe=One-Click`, in a dictionary the host adds its other headers to. The URL is
the host's: an https address (no credentials, no comma) that unsubscribes on a POST alone, with no cookie, no sign-in
and no redirect, so it carries its own token. Resend signs the mail with DKIM over these headers, as RFC 8058 requires.
Keep a visible unsubscribe link in the body too.

## Delivery webhooks

```csharp
if (ResendWebhooks.SecretRefusal(secret) is { } why) throw new InvalidOperationException(why);   // at startup

var check = ResendWebhooks.Verify(secret, headers["svix-id"], headers["svix-timestamp"], headers["svix-signature"], rawBody, now);
if (check != ResendWebhookCheck.Valid) return Results.Unauthorized();
if (ResendWebhooks.Parse(rawBody) is { IsHardBounce: true } e) { /* suppress e.To */ }
```

- Resend signs through Svix, whose scheme is Standard Webhooks v1 under `svix-` header names: HMAC-SHA256 over
  `id.timestamp.body`, keyed by the `whsec_` secret, `v1,<base64>` entries separated by spaces. `Verify` checks the
  header shapes (`Malformed`), the timestamp within five minutes either side (`Stale`), then the signatures with
  AgentKit.Channels' `StandardWebhooks` (`Valid` or `Mismatch`, constant-time). Pinned to Svix's published example.
- Read the raw body (bounded) before parsing anything; the signature is over its bytes. A header sent twice is the
  host's to refuse. A retry carries the same `svix-id`: drop repeats by it.
- `Parse` gives `ResendWebhookEvent`: `Type` (`ResendEventTypes`), `CreatedAt`, `EmailId`, `To` (a string or a list,
  as written), `BounceType` / `BounceSubType` / `BounceMessage`, `Email` and `Origin` for suppression events,
  `IsHardBounce` (`Permanent` only, never "not soft"), and `Data`, the whole `data` object. An unknown type still
  reads; a body that isn't an event is null.

## Stays out

Templates, address books, suppression lists, preferences, a dev outbox, cc, bcc, attachments, tags and scheduling.
Those are the host's.
